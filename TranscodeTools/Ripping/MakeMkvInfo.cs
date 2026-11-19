// ============================================================
// MakeMkvInfo.cs
// ------------------------------------------------------------
// The info stage: `makemkvcon -r --noscan --minlength=0 info <source>`
// run against the backup, logged to
// <Destination>\Logs\<timestamp>\<DISC>\info.log, and its TINFO
// lines read into the disc's titles. The source is `iso:<file>` for
// a DVD's image and `file:<folder>` for a Blu-ray's BDMV folder.
//
// TINFO:row,field,code,"value" -- one line per field of each title
// MakeMKV lists. The row is MakeMKV's own title number, the one its
// `mkv` command takes. The fields used:
//    8  chapter count (the chapter check)
//    9  duration, "h:mm:ss"
//   11  the title's size on the disc, in bytes (the rip's size guard;
//       a DVD rip comes out at 0.978-0.981 of it)
//   16  the source it was built from: a playlist ("00800.mpls") or a
//       loose clip ("50201.m2ts"); the Blu-ray key
//   24  the DVD title number it comes from (the DVD key)
//   26  the cell segments it covers, e.g. "1-5,6-9" (play-all check)
//   27  the file name its rip will be written under
//
// DVD: the record already holds the title table from the IFO; each
// row is joined to its title by field 24, never by row number
// (--minlength can drop rows). MSG:3027 lines name the titles MakeMKV
// skipped as copies of another ("Title #36 in VTS 10 is equal to title
// #35 and was skipped"): a skipped title has no row and gets SameAs.
// The chapter check: MakeMKV drops the trailing menu-jump cell, so its
// count is the title table's or one fewer.
//
// Blu-ray: there is no title table, so the titles are made from the
// rows, one per row, keyed by field 16. The chapter check runs
// mkvmerge on each row's playlist (PlaylistChapters): MakeMKV's count
// must equal mkvmerge's or be exactly one higher. MakeMKV adds a
// chapter at the very end of a title (a known MakeMKV behaviour; see
// Known Issues in the reference doc); measured 2026-11-18, every
// mismatch on five Blu-rays was MakeMKV one higher, never any other
// difference. A row built from a loose clip has no playlist to check
// against, so it stays unverified, with the reason in its note and the
// log.
//
// A title that fails its check stays Verified = false and is never
// bound to a file. Every title or row that could not be joined is
// written to the log and reported; nothing is guessed. Then a title
// made of several segment ranges whose runtime is the sum of 2-4
// other titles (within 2 s) is flagged as a play-all, not hidden.
//
// --noscan keeps MakeMKV off the optical drives: without it makemkvcon
// probes every drive at startup even when reading a backup.
// ============================================================

using System.IO;
using System.Text;

namespace TranscodeTools;

public static class MakeMkvInfo
{
    // The rip must pass the same value: MakeMKV numbers its titles, and so
    // names its files, after this filter has run.
    public const int MinLength = 0;

    public const string LogFileName = "info.log";

    private const int CompositeToleranceSeconds = 2;

    private const string SkippedAsEqualMessage = "3027";

    private const string PlaylistExtension = ".mpls";

    // One title as MakeMKV lists it. SourceTitle is field 24, which only
    // a DVD's rows carry.
    public sealed record InfoTitle(
        int    Row,
        int?   SourceTitle,
        string Source,
        int    Chapters,
        string Duration,
        int    Seconds,
        long   SourceBytes,
        string Segments,
        string RipName);

    // Runs the info stage on the backup and fills the record's titles from
    // it: the DVD title table's titles, or for a Blu-ray new titles from
    // MakeMKV's rows. Throws MakeMkvException on failure,
    // OperationCanceledException on cancel. Anything that could not be
    // joined or checked is reported through `progress` and the log.
    public static async Task RunAsync(
        string makeMkvPath,
        string mkvMergePath,
        string backupPath,
        string logPath,
        DiscRecord record,
        IProgress<string> progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        log.WriteLine("TranscodeTools Info Log");
        log.WriteLine($"Run:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.WriteLine($"Disc:   {record.Name}");
        log.WriteLine($"Backup: {backupPath}");
        log.WriteLine(new string('-', 60));
        log.WriteLine();

        try
        {
            var source = record.BluRay ? $"file:{backupPath}" : $"iso:{backupPath}";
            var args   = new[] { "-r", "--noscan", $"--minlength={MinLength}", "info", source };
            var result = await MakeMkv.RunAsync(makeMkvPath, args, log, progress: null, token);

            if (result.ExitCode != 0)
                throw MakeMkv.ExitFailure(result);

            var titles = Parse(result.Lines);
            if (titles.Count == 0)
                throw new MakeMkvException("MakeMKV listed no titles.");

            var problems = new List<string>();
            if (record.BluRay)
            {
                log.WriteLine();
                log.WriteLine("Chapter check (mkvmerge)");
                problems.AddRange(await ApplyBluRayAsync(record, titles, mkvMergePath, backupPath, log, progress, token));
            }
            else
            {
                var skipped = ParseSkipped(result.Lines, problems);
                problems.AddRange(ApplyDvd(record, titles, skipped));
            }
            FlagPlayAll(record);

            log.WriteLine();
            foreach (var problem in problems)
            {
                log.WriteLine(problem);
                progress.Report($"skipped: {problem}");
            }
            log.WriteLine($"Info complete: {titles.Count} title(s) listed, " +
                          $"{record.Titles.Count(t => t.RipName != null)} joined, " +
                          $"{record.Titles.Count(t => t.Verified)} passed the chapter check");
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
    }

    // Reads the TINFO lines into one entry per row.
    public static List<InfoTitle> Parse(IEnumerable<string> lines)
    {
        var rows = new SortedDictionary<int, Dictionary<int, string>>();

        foreach (var line in lines)
        {
            if (!line.StartsWith("TINFO:", StringComparison.Ordinal)) continue;

            var fields = RobotLine.Split(line["TINFO:".Length..]);
            if (fields.Count < 4) continue;
            if (!int.TryParse(fields[0], out var row) || !int.TryParse(fields[1], out var id)) continue;

            if (!rows.TryGetValue(row, out var values))
                rows[row] = values = new Dictionary<int, string>();
            values[id] = fields[3];
        }

        return rows.Select(r =>
        {
            var duration = Get(r.Value, 9);
            return new InfoTitle(
                Row:         r.Key,
                SourceTitle: int.TryParse(Get(r.Value, 24).Trim(), out var source) ? source : null,
                Source:      Get(r.Value, 16),
                Chapters:    int.TryParse(Get(r.Value, 8), out var chapters) ? chapters : 0,
                Duration:    duration,
                Seconds:     Seconds(duration),
                SourceBytes: Bytes(Get(r.Value, 11)),
                Segments:    Get(r.Value, 26),
                RipName:     Get(r.Value, 27));
        }).ToList();
    }

    // Reads the MSG:3027 lines into skipped title -> the title MakeMKV kept
    // in its place. The parameters are the title set, the kept title and
    // the skipped title, the titles written "#35". A 3027 line that can't
    // be read that way is added to `problems`.
    public static Dictionary<int, int> ParseSkipped(IEnumerable<string> lines, List<string> problems)
    {
        var skipped = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            if (!line.StartsWith("MSG:", StringComparison.Ordinal)) continue;

            var fields = RobotLine.Split(line["MSG:".Length..]);
            if (fields[0] != SkippedAsEqualMessage) continue;

            if (fields.Count >= 8 &&
                int.TryParse(fields[6].TrimStart('#'), out var kept) &&
                int.TryParse(fields[7].TrimStart('#'), out var copy))
                skipped[copy] = kept;
            else
                problems.Add($"MakeMKV message {SkippedAsEqualMessage} could not be read: {line}");
        }
        return skipped;
    }

    // DVD: fills each title-table title from the row for its title number
    // and runs the chapter check. A title MakeMKV skipped as a copy gets
    // SameAs instead of a row. Returns a line for every title or row that
    // could not be joined, and every skipped title.
    private static List<string> ApplyDvd(DiscRecord record, List<InfoTitle> titles, Dictionary<int, int> skipped)
    {
        var problems = new List<string>();
        var bySource = new Dictionary<int, InfoTitle>();
        foreach (var info in titles)
        {
            if (info.SourceTitle is not int number)
            {
                problems.Add($"MakeMKV row {info.Row} names no DVD title");
                continue;
            }
            if (!bySource.TryAdd(number, info))
                throw new MakeMkvException($"MakeMKV lists title {number} more than once.");
        }

        foreach (var title in record.Titles)
        {
            title.SameAs = null;
            if (!bySource.TryGetValue(title.Number, out var info))
            {
                if (skipped.TryGetValue(title.Number, out var kept))
                {
                    title.SameAs = kept;
                    problems.Add($"title {title.Number}: skipped by MakeMKV, same as title {kept}");
                }
                else
                {
                    problems.Add($"title {title.Number}: MakeMKV lists no title for it");
                }
                continue;
            }

            Fill(title, info);
            title.Verified = info.Chapters == title.Chapters || info.Chapters == title.Chapters - 1;
            if (!title.Verified)
                title.Note = $"chapter mismatch: disc says {title.Chapters}, MakeMKV says {info.Chapters}";
        }

        var known = record.Titles.Select(t => t.Number).ToHashSet();
        foreach (var (number, info) in bySource.Where(s => !known.Contains(s.Key)))
            problems.Add($"MakeMKV row {info.Row} comes from title {number}, which is not in the disc's title table");
        foreach (var (copy, kept) in skipped.Where(s => !known.Contains(s.Key)))
            problems.Add($"MakeMKV says it skipped title {copy} as the same as title {kept}, " +
                         "but that title is not in the disc's title table");
        foreach (var copy in skipped.Keys.Where(bySource.ContainsKey))
            problems.Add($"MakeMKV says it skipped title {copy}, but it also lists a row for it; the row is used");

        return problems;
    }

    // Blu-ray: one title per row, then the chapter check against mkvmerge's
    // count for the row's playlist. Each check is written to the log.
    private static async Task<List<string>> ApplyBluRayAsync(
        DiscRecord record, List<InfoTitle> titles, string mkvMergePath, string backupPath,
        StreamWriter log, IProgress<string> progress, CancellationToken token)
    {
        var problems = new List<string>();
        record.Titles = titles.Select(info =>
        {
            var title = new TitleRecord { Number = info.Row + 1 };
            Fill(title, info);
            return title;
        }).ToList();

        foreach (var (title, info) in record.Titles.Zip(titles))
        {
            token.ThrowIfCancellationRequested();

            if (!info.Source.EndsWith(PlaylistExtension, StringComparison.OrdinalIgnoreCase))
            {
                title.Note = info.Source.Length == 0
                    ? "MakeMKV named no source for it, so its chapters could not be checked"
                    : $"built from clip {info.Source}, with no playlist to check its chapters against";
                problems.Add($"title {title.Number}: {title.Note}");
                continue;
            }

            progress.Report($"checking chapters of {info.Source}");
            var mpls = Path.Combine(backupPath, "BDMV", "PLAYLIST", info.Source);
            title.Chapters = await PlaylistChapters.ReadAsync(mkvMergePath, mpls, token);
            // MakeMKV's extra end chapter: one higher is the same title.
            title.Verified = info.Chapters == title.Chapters || info.Chapters == title.Chapters + 1;
            log.WriteLine($"title {title.Number} {info.Source}: MakeMKV {info.Chapters}, mkvmerge {title.Chapters}" +
                          (!title.Verified                      ? " - MISMATCH"
                           : info.Chapters == title.Chapters + 1 ? " (MakeMKV's extra end chapter)"
                           :                                       ""));
            if (!title.Verified)
                title.Note = $"chapter mismatch: {info.Source} has {title.Chapters}, MakeMKV says {info.Chapters}";
        }
        return problems;
    }

    // What every row adds to its title, on either kind of disc.
    private static void Fill(TitleRecord title, InfoTitle info)
    {
        title.MakeMkvTitle = info.Row;
        title.Playlist     = info.Source.Length > 0 ? info.Source : null;
        title.Duration     = info.Duration;
        title.Seconds      = info.Seconds;
        title.SourceBytes  = info.SourceBytes;
        title.Segments     = info.Segments;
        title.RipName      = info.RipName;
    }

    // A title with several segment ranges whose runtime equals the sum of
    // 2, 3 or 4 other titles is a play-all. The first combination found
    // is kept, smallest first, as the oracle does.
    private static void FlagPlayAll(DiscRecord record)
    {
        var timed = record.Titles.Where(t => t.RipName != null && t.Seconds > 0).ToList();

        foreach (var title in timed.Where(t => t.Segments.Contains(',')))
        {
            var others = timed.Where(t => t.Number != title.Number).ToList();
            for (int size = 2; size <= 4 && title.CompositeOf.Count == 0; size++)
            {
                var combo = Combinations(others, size).FirstOrDefault(c =>
                    Math.Abs(c.Sum(t => t.Seconds) - title.Seconds) <= CompositeToleranceSeconds);
                if (combo == null) continue;

                title.CompositeOf    = combo.Select(t => t.Number).OrderBy(n => n).ToList();
                title.Flags.PlayAll  = true;
                var playAll = $"play-all: same runtime as titles {string.Join("+", title.CompositeOf)}";
                title.Note = title.Note.Length == 0 ? playAll : $"{playAll}; {title.Note}";
            }
        }
    }

    // Every way to pick `size` items from `items`, in order.
    private static IEnumerable<List<TitleRecord>> Combinations(List<TitleRecord> items, int size, int start = 0)
    {
        if (size == 0) { yield return new List<TitleRecord>(); yield break; }
        for (int i = start; i <= items.Count - size; i++)
            foreach (var rest in Combinations(items, size - 1, i + 1))
            {
                rest.Insert(0, items[i]);
                yield return rest;
            }
    }

    private static string Get(Dictionary<int, string> values, int id) =>
        values.TryGetValue(id, out var v) ? v : "";

    // "h:mm:ss" to seconds; 0 when it is not in that form.
    private static int Seconds(string duration)
    {
        int total = 0;
        foreach (var part in duration.Split(':'))
        {
            if (!int.TryParse(part, out var n)) return 0;
            total = total * 60 + n;
        }
        return total;
    }

    // Field 11 is the exact byte count. Field 10 is the human-readable
    // size ("4.4 GB") and is no use here.
    private static long Bytes(string value)
    {
        var digits = value.Replace(",", "").Replace(" ", "").Trim();
        return long.TryParse(digits, out var n) ? n : 0;
    }
}
