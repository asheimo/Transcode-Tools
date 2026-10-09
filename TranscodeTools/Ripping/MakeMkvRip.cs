// ============================================================
// MakeMkvRip.cs
// ------------------------------------------------------------
// The rip stage: one `makemkvcon -r --noscan --minlength=0 mkv iso:<ISO> all
// <Rip\DISC>` run writes every title as an .mkv, then the binder
// (RipBinder.cs) ties each disc title to its file. Both are logged to
// <Destination>\Logs\<timestamp>\<DISC>\rip.log.
//
// --noscan keeps MakeMKV off the optical drives: without it makemkvcon
// probes every drive at startup even when reading an ISO.
//
// --minlength is the info run's (MakeMkvInfo.MinLength): MakeMKV
// numbers its titles, and so names its files, after that filter, and
// the binder's key is the name the info run reported.
//
// MEASURED (2026-08-14): a successful mkv run names no files in its
// output, and MakeMKV does not create the destination folder. So the
// folder is created here, and success is the exit code; which files
// exist is the binder's business.
//
// The rip only starts into a folder with no .mkv in it (RipView asks
// before overwriting old rips and deletes them on Yes), so on failure
// or cancel every .mkv there is this run's, and they are deleted: a half-written rip left behind
// would look like a finished one, the same reason a partial ISO goes.
// ============================================================

using System.IO;
using System.Text;

namespace TranscodeTools;

public static class MakeMkvRip
{
    public const string LogFileName = "rip.log";

    // Rips the ISO into `ripFolder` and binds the record's titles to the
    // files. Throws MakeMkvException on failure, OperationCanceledException
    // on cancel.
    public static async Task RunAsync(
        string makeMkvPath,
        string isoPath,
        string ripFolder,
        string logPath,
        DiscRecord record,
        IProgress<double> progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(ripFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        log.WriteLine("TranscodeTools Rip Log");
        log.WriteLine($"Run:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.WriteLine($"Disc:   {record.Name}");
        log.WriteLine($"ISO:    {isoPath}");
        log.WriteLine($"Folder: {ripFolder}");
        log.WriteLine(new string('-', 60));
        log.WriteLine();

        bool succeeded = false;
        try
        {
            var args = new[]
            {
                "-r", "--noscan", $"--minlength={MakeMkvInfo.MinLength}", "--progress=-same",
                "mkv", $"iso:{isoPath}", "all", ripFolder,
            };
            var result = await MakeMkv.RunAsync(makeMkvPath, args, log, progress, token);

            if (result.ExitCode != 0)
                throw MakeMkv.ExitFailure(result);

            log.WriteLine();
            log.WriteLine("Binding");
            foreach (var line in RipBinder.Bind(record, ripFolder))
                log.WriteLine(line);

            log.WriteLine();
            log.WriteLine($"Rip complete: {record.Titles.Count(t => t.Bound)} of {record.Titles.Count} titles bound");
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            log.WriteLine();
            log.WriteLine("Cancelled.");
            throw;
        }
        catch (Exception ex) when (ex is MakeMkvException or IOException or UnauthorizedAccessException)
        {
            log.WriteLine();
            log.WriteLine($"Failed: {ex.Message}");
            throw;
        }
        finally
        {
            if (!succeeded)
                DeletePartialRips(ripFolder, log);
        }
    }

    private static void DeletePartialRips(string ripFolder, StreamWriter log)
    {
        foreach (var file in Directory.GetFiles(ripFolder, "*.mkv"))
        {
            try
            {
                File.Delete(file);
                log.WriteLine($"Partial rip deleted: {file}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.WriteLine($"Partial rip could not be deleted: {file}: {ex.Message}");
            }
        }
    }
}
