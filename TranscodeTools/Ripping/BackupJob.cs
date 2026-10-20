// ============================================================
// BackupJob.cs
// ------------------------------------------------------------
// The first pipeline stage: one decrypted backup of a disc, made
// by makemkvcon in robot mode (-r), written as
// <Destination>\ISO\<DISC>.iso, logged to
// <Destination>\Logs\Backup\<DISC>.log.
//
// Two makemkvcon runs (through MakeMkv.RunAsync):
//   1. `-r --cache=1 info disc:9999` lists the drives. MakeMKV
//      addresses drives by its own index, not by drive letter, and
//      the index differs from machine to machine, so it is looked
//      up by letter on every backup rather than stored.
//   2. `backup --decrypt -r --progress=-same disc:N <path>` makes
//      the image. MakeMKV's docs call <path> a destination folder.
//      For a DVD, a path without a trailing backslash becomes the
//      image file itself (measured with MakeMKV 1.18.3), so the
//      path passed is the finished ISO's own name.
//
// Success is makemkvcon exiting with 0 AND the ISO existing. The
// docs do not say what a successful backup prints, so no message
// text is relied on. On failure or cancel the partial ISO is
// deleted: left in ISO\ it would look like a finished backup.
//
// Runs off the UI thread. Progress is reported through
// IProgress<MakeMkvProgress>, created on the UI thread by the caller.
// ============================================================

using System.IO;
using System.Text;

namespace TranscodeTools;

public static class BackupJob
{
    // Runs the whole backup for the disc in `driveLetter` ("D:").
    // Throws MakeMkvException on failure, OperationCanceledException on
    // cancel; in both cases the partial ISO has been removed.
    public static async Task RunAsync(
        string makeMkvPath,
        string driveLetter,
        string discName,
        string isoPath,
        string logPath,
        IProgress<MakeMkvProgress> progress,
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
            var result = await MakeMkv.RunAsync(makeMkvPath, args, log, progress, token);

            if (result.ExitCode != 0)
                throw MakeMkv.ExitFailure(result);

            if (!File.Exists(isoPath))
                throw new MakeMkvException($"MakeMKV finished but {isoPath} was not created.");

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
        catch (MakeMkvException ex)
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
        var result = await MakeMkv.RunAsync(makeMkvPath, args, log, progress: null, token);

        var driveLines = result.Lines.Where(l => l.StartsWith("DRV:", StringComparison.Ordinal)).ToList();

        var want = driveLetter.TrimEnd('\\');
        foreach (var line in driveLines)
        {
            var fields = RobotLine.Split(line["DRV:".Length..]);
            if (fields.Count < 7) continue;
            if (!fields[6].TrimEnd('\\').Equals(want, StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(fields[0], out var index))
                throw new MakeMkvException($"MakeMKV listed {want} with an unreadable index: {line}");

            log.WriteLine();
            log.WriteLine($"{want} is MakeMKV disc:{index}");
            log.WriteLine();
            return index;
        }

        throw new MakeMkvException(driveLines.Count == 0
            ? "MakeMKV did not list any drives."
            : $"MakeMKV did not list drive {want}.");
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
}
