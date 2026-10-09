// ============================================================
// MakeMkv.cs
// ------------------------------------------------------------
// Running makemkvcon in robot mode (-r) and reading what it
// prints. Shared by every stage that calls MakeMKV: the backup
// (BackupJob) and the info run (MakeMkvInfo); the rip joins them.
//
// Robot mode prints one record per line, "TYPE:field,field,...".
// Every line except PRGV goes to the stage's log and is kept for
// the caller to read. PRGV lines arrive many times a second, so
// they drive a progress fraction instead of burying the log.
// ============================================================

using System.Diagnostics;
using System.IO;
using System.Text;

namespace TranscodeTools;

// A MakeMKV stage that did not finish. The message is shown to the
// user, so it says what happened in plain words.
public sealed class MakeMkvException : Exception
{
    public MakeMkvException(string message) : base(message) { }
}

public sealed class MakeMkvResult
{
    public int ExitCode { get; set; } = -1;

    // Every line printed except PRGV, in order.
    public List<string> Lines { get; } = new();

    // The text of the last MSG line: what MakeMKV said last, used in
    // a failure's message.
    public string LastMessage { get; set; } = "";
}

public static class MakeMkv
{
    // Runs makemkvcon with `args`, writes its output to `log` and returns
    // what it printed. Throws MakeMkvException if it cannot be started,
    // OperationCanceledException on cancel (the process is killed and
    // waited for first, so the caller can clean up its files).
    public static async Task<MakeMkvResult> RunAsync(
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
                result.Lines.Add(line);

                if (line.StartsWith("MSG:", StringComparison.Ordinal))
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
            throw new MakeMkvException($"MakeMKV could not be started from {makeMkvPath}: {ex.Message}");
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
            // Wait for it to be gone, so the caller can delete its files.
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        // WaitForExitAsync returns once the process ends; this call with
        // no timeout also waits for the redirected output to be drained.
        process.WaitForExit();

        result.ExitCode = process.ExitCode;
        return result;
    }

    // A non-zero exit, worded with MakeMKV's own last message when it
    // printed one.
    public static MakeMkvException ExitFailure(MakeMkvResult result) =>
        new(result.LastMessage.Length > 0
            ? $"MakeMKV exited with code {result.ExitCode}: {result.LastMessage}"
            : $"MakeMKV exited with code {result.ExitCode}.");

    // PRGV:current,total,max -- the total bar is the whole run.
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
