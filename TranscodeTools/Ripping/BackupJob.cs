// ============================================================
// BackupJob.cs
// ------------------------------------------------------------
// The first pipeline stage: one decrypted backup of a disc, made
// by makemkvcon in robot mode (-r), written as
// <Destination>\ISO\<DISC>.iso, logged to
// <Destination>\Logs\Backup\<DISC>.log.
//
// Two makemkvcon runs:
//   1. `-r --cache=1 info disc:9999` lists the drives. MakeMKV
//      addresses drives by its own index, not by drive letter, and
//      the index differs from machine to machine, so it is looked
//      up by letter on every backup rather than stored.
//   2. `backup --decrypt -r --progress=-same disc:N <path>` makes
//      the image. MakeMKV's docs call <path> a destination folder.
//      For a DVD, a path without a trailing backslash is reported
//      (forum, not docs) to become the image file itself, so the
//      path passed is the finished ISO's own name.
//
// Success is makemkvcon exiting with 0 AND the ISO existing. The
// docs do not say what a successful backup prints, so no message
// text is relied on. On failure or cancel the partial ISO is
// deleted: left in ISO\ it would look like a finished backup.
//
// Runs off the UI thread. Progress is reported as a fraction 0..1
// through IProgress<double>, created on the UI thread by the caller.
// ============================================================

using System.Diagnostics;
using System.IO;
using System.Text;

namespace TranscodeTools;

// A backup that did not produce an ISO. The message is shown to the
// user, so it says what happened in plain words.
public sealed class BackupException : Exception
{
    public BackupException(string message) : base(message) { }
}

public static class BackupJob
{
    // Runs the whole backup for the disc in `driveLetter` ("D:").
    // Throws BackupException on failure, OperationCanceledException on
    // cancel; in both cases the partial ISO has been removed.
    public static async Task RunAsync(
        string makeMkvPath,
        string driveLetter,
        string discName,
        string isoPath,
        string logPath,
        IProgress<double> progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(isoPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        // Overwritten by a repeat: one log per disc, the latest attempt.
        await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        log.WriteLine("TranscodeTools Backup Log");
        log.WriteLine($"Run:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.WriteLine($"Disc:  {discName}");
        log.WriteLine($"Drive: {driveLetter}");
        log.WriteLine(new string('-', 60));
        log.WriteLine();

        bool succeeded = false;
        try
        {
            var index = await FindDiscIndexAsync(makeMkvPath, driveLetter, log, token);

            var args = new[] { "backup", "--decrypt", "-r", "--progress=-same", $"disc:{index}", isoPath };
            var result = await RunMakeMkvAsync(makeMkvPath, args, log, progress, token);

            if (result.ExitCode != 0)
                throw new BackupException(result.LastMessage.Length > 0
                    ? $"MakeMKV exited with code {result.ExitCode}: {result.LastMessage}"
                    : $"MakeMKV exited with code {result.ExitCode}.");

            if (!File.Exists(isoPath))
                throw new BackupException($"MakeMKV finished but {isoPath} was not created.");

            log.WriteLine();
            log.WriteLine($"Backup complete: {isoPath} ({new FileInfo(isoPath).Length:N0} bytes)");
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            log.WriteLine();
            log.WriteLine("Cancelled.");
            throw;
        }
        catch (BackupException ex)
        {
            log.WriteLine();
            log.WriteLine($"Failed: {ex.Message}");
            throw;
        }
        finally
        {
            if (!succeeded)
                DeletePartialIso(isoPath, log);
        }
    }

    // ── Drive lookup ─────────────────────────────────────────────────
    // DRV:index,visible,enabled,flags,"drive name","disc name","D:"
    // The docs list six fields; the drive letter is a seventh field
    // seen in real output. A drive whose line carries no letter, or no
    // line for the letter at all, refuses the backup rather than
    // falling back to an index.

    private static async Task<int> FindDiscIndexAsync(
        string makeMkvPath, string driveLetter, StreamWriter log, CancellationToken token)
    {
        var args   = new[] { "-r", "--cache=1", "info", "disc:9999" };
        var result = await RunMakeMkvAsync(makeMkvPath, args, log, progress: null, token);

        var want = driveLetter.TrimEnd('\\');
        foreach (var line in result.DriveLines)
        {
            var fields = RobotLine.Split(line["DRV:".Length..]);
            if (fields.Count < 7) continue;
            if (!fields[6].TrimEnd('\\').Equals(want, StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(fields[0], out var index))
                throw new BackupException($"MakeMKV listed {want} with an unreadable index: {line}");

            log.WriteLine();
            log.WriteLine($"{want} is MakeMKV disc:{index}");
            log.WriteLine();
            return index;
        }

        throw new BackupException(result.DriveLines.Count == 0
            ? "MakeMKV did not list any drives."
            : $"MakeMKV did not list drive {want}.");
    }

    // ── Running makemkvcon ───────────────────────────────────────────

    private sealed class MakeMkvResult
    {
        public int          ExitCode    { get; set; } = -1;
        public string       LastMessage { get; set; } = "";
        public List<string> DriveLines  { get; } = new();
    }

    // Runs makemkvcon, writes its output to the log and returns the exit
    // code, the last MSG line's text and any DRV lines. PRGV lines drive
    // the progress fraction and are left out of the log: they arrive
    // many times a second and would bury everything else.
    private static async Task<MakeMkvResult> RunMakeMkvAsync(
        string makeMkvPath,
        IReadOnlyList<string> args,
        StreamWriter log,
        IProgress<double>? progress,
        CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = makeMkvPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        // ArgumentList quotes each argument itself, so a Destination
        // with spaces needs no hand-built quoting.
        foreach (var a in args) psi.ArgumentList.Add(a);

        log.WriteLine($"> \"{makeMkvPath}\" {string.Join(' ', args.Select(Quote))}");

        var result = new MakeMkvResult();
        var gate   = new object();

        using var process = new Process { StartInfo = psi };

        void OnLine(string? line)
        {
            if (line == null) return;

            if (line.StartsWith("PRGV:", StringComparison.Ordinal))
            {
                if (progress != null && TryReadProgress(line, out var fraction))
                    progress.Report(fraction);
                return;
            }

            lock (gate)
            {
                log.WriteLine(line);

                if (line.StartsWith("DRV:", StringComparison.Ordinal))
                    result.DriveLines.Add(line);
                else if (line.StartsWith("MSG:", StringComparison.Ordinal))
                {
                    var fields = RobotLine.Split(line["MSG:".Length..]);
                    if (fields.Count >= 4) result.LastMessage = fields[3];
                }
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived  += (_, e) => OnLine(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BackupException($"MakeMKV could not be started from {makeMkvPath}: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // Wait for it to be gone, so the partial ISO can be deleted.
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        // WaitForExitAsync returns once the process ends; this call with
        // no timeout also waits for the redirected output to be drained.
        process.WaitForExit();

        result.ExitCode = process.ExitCode;
        return result;
    }

    // PRGV:current,total,max -- the total bar is the whole backup.
    private static bool TryReadProgress(string line, out double fraction)
    {
        fraction = 0;
        var parts = line["PRGV:".Length..].Split(',');
        if (parts.Length != 3) return false;
        if (!long.TryParse(parts[1], out var total) || !long.TryParse(parts[2], out var max)) return false;
        if (max <= 0) return false;
        fraction = Math.Clamp((double)total / max, 0, 1);
        return true;
    }

    private static void DeletePartialIso(string isoPath, StreamWriter log)
    {
        if (!File.Exists(isoPath)) return;
        try
        {
            File.Delete(isoPath);
            log.WriteLine($"Partial image deleted: {isoPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Partial image could not be deleted: {isoPath}: {ex.Message}");
        }
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}

// Splits the body of a robot-mode line (after "DRV:", "MSG:" ...) into
// fields. Fields are comma-separated; strings are double-quoted, with
// quotes and control characters backslash-escaped inside them.
public static class RobotLine
{
    public static List<string> Split(string body)
    {
        var fields  = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < body.Length) { current.Append(body[++i]); continue; }
                if (c == '"') { quoted = false; continue; }
                current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
