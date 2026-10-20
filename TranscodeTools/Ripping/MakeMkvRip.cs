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
// Progress names the file being saved: (2/6) and both of MakeMKV's
// bars. MEASURED (MakeMKV 1.18.3, THE_CENTENNIAL rip.log 2026-10-20):
// the rip prints MSG:5014 "Saving %1 titles into directory %2" once, with
// the file count as its first parameter, then for each file, numbered
// from 0, PRGC:5057 "Analyzing seamless segments" and PRGC:5017 "Saving
// to MKV file".
//
// The rip only starts into a folder with no .mkv in it (RipView asks
// before overwriting old rips and deletes them on Yes), so on failure
// or cancel every .mkv there is this run's, and they are deleted: a half-written rip left behind
// would look like a finished one, the same reason a partial ISO goes.
// ============================================================

using System.IO;
using System.Text;

namespace TranscodeTools;

// Where a rip is: file `File` of `Files` (0 of 0 until MakeMKV has said),
// with MakeMKV's bar for that file and for the whole rip, each 0..1.
public sealed record RipProgress(int File, int Files, double Current, double Total);

public static class MakeMkvRip
{
    public const string LogFileName = "rip.log";

    private const string SavingTitlesMessage = "5014";
    private static readonly string[] FileProgressCodes = { "5057", "5017" };

    // Rips the ISO into `ripFolder` and binds the record's titles to the
    // files. Throws MakeMkvException on failure, OperationCanceledException
    // on cancel.
    public static async Task RunAsync(
        string makeMkvPath,
        string isoPath,
        string ripFolder,
        string logPath,
        DiscRecord record,
        IProgress<RipProgress> progress,
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
            // Both are written by the thread reading MakeMKV's output (OnLine)
            // and read on the same thread when a PRGV line reports progress.
            int file = 0, files = 0;
            void OnLine(string line)
            {
                if (line.StartsWith("MSG:", StringComparison.Ordinal))
                {
                    var fields = RobotLine.Split(line["MSG:".Length..]);
                    if (fields.Count > 5 && fields[0] == SavingTitlesMessage && int.TryParse(fields[5], out var count))
                        files = count;
                }
                else if (line.StartsWith("PRGC:", StringComparison.Ordinal))
                {
                    var fields = RobotLine.Split(line["PRGC:".Length..]);
                    if (fields.Count > 1 && FileProgressCodes.Contains(fields[0]) && int.TryParse(fields[1], out var index))
                        file = index + 1;
                }
            }
            var bars = new Relay<MakeMkvProgress>(p => progress.Report(new RipProgress(file, files, p.Current, p.Total)));

            var result = await MakeMkv.RunAsync(makeMkvPath, args, log, bars, token, OnLine);

            if (result.ExitCode != 0)
                throw MakeMkv.ExitFailure(result);

            log.WriteLine();
            log.WriteLine("Binding");
            foreach (var line in RipBinder.Bind(record, ripFolder))
                log.WriteLine(line);

            log.WriteLine();
            // Listed after binding, so unclaimed files carry their prefix.
            record.RipFiles = Directory.GetFiles(ripFolder, "*.mkv")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new RipFileRecord { Name = f.Name, Bytes = f.Length })
                .ToList();
            log.WriteLine($"Rip complete: {record.Titles.Count(t => t.Bound)} of {record.Titles.Count} titles bound, " +
                          $"{record.RipFiles.Count} file(s) written");
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

    // Passes each report straight on, on the reporting thread. The caller's
    // own IProgress takes it to the UI thread.
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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
