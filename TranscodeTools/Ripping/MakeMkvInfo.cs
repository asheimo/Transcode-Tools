// ============================================================
// MakeMkvInfo.cs
// ------------------------------------------------------------
// The info stage (port seam 5): `makemkvcon -r --noscan --minlength=0 info
// iso:<ISO>` run against the backup ISO, logged to
// <Destination>\Logs\<timestamp>\<DISC>\info.log, and its TINFO
// lines read into the disc's titles.
//
// TINFO:row,field,code,"value" -- one line per field of each title
// MakeMKV lists. The fields used:
//    8  chapter count (the chapter gate: TT_SRPT's count or one fewer)
//    9  duration, "h:mm:ss"
//   11  the title's size on the disc, in bytes (the binder's size
//       guard; the ripped file comes out at 0.978-0.981 of it)
//   24  the DVD title number it comes from (the join to TT_SRPT)
//   26  the cell segments it covers, e.g. "1-5,6-9" (play-all check)
//   27  the file name the rip will be written under (the binder's key)
//
// MSG:3027 lines name the titles MakeMKV skipped as copies of another:
// "Title #36 in VTS 10 is equal to title #35 and was skipped", with the
// title set, the kept title and the skipped title as its parameters. A
// skipped title has no TINFO row; it gets SameAs set to the kept title,
// so the buttons that name it can be shown on the kept title's file.
//
// Rows are joined to the disc's titles by field 24, never by row
// number: --minlength can drop rows and shift the numbering. A title
// MakeMKV does not list, or a row naming a title the disc does not
// have, is written to the log and reported; nothing is guessed.
//
// Once joined, each title gets the oracle's two checks (join_titles):
//   - the chapter gate: MakeMKV drops the trailing menu-jump cell, so
//     its count is TT_SRPT's or one fewer. Anything else means the row
//     is not this title, and Verified stays false so it is never bound.
//   - play-all: a title made of several segment ranges whose runtime is
//     the sum of 2-4 other titles (within 2 s) is flagged, not hidden.
//
// The ISO is read by MakeMKV directly (iso:), so it is not mounted.
// --noscan keeps MakeMKV off the optical drives: without it makemkvcon
// probes every drive at startup even when reading an ISO.
// ============================================================

using System.IO;
using System.Text;

namespace TranscodeTools;

public static class MakeMkvInfo
{
    // The rip stage must pass the same value: MakeMKV numbers its titles,
    // and so names its files, after this filter has run.
    public const int MinLength = 0;

    public const string LogFileName = "info.log";

    private const int CompositeToleranceSeconds = 2;

    private const string SkippedAsEqualMessage = "3027";

    // One title as MakeMKV lists it.
    public sealed record InfoTitle(
        int    Row,
        int    SourceTitle,
        int    Chapters,
        string Duration,
        int    Seconds,
        long   SourceBytes,
        string Segments,
        string RipName);

    // Runs the info stage for the ISO and fills each of the record's
    // titles from it. Throws MakeMkvException on failure,
    // OperationCanceledException on cancel. Anything that could not be
    // joined is reported through `progress` and the log.
    public static async Task RunAsync(
        string makeMkvPath,
        string isoPath,
        string logPath,
        DiscRecord record,
        IProgress<string> progress,
        CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        await using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        log.WriteLine("TranscodeTools Info Log");
        log.WriteLine($"Run:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.WriteLine($"Disc: {record.Name}");
        log.WriteLine($"ISO:  {isoPath}");
        log.WriteLine(new string('-', 60));
        log.WriteLine();

        try
        {
            var args = new[] { "-r", "--noscan", $"--minlength={MinLength}", "info", $"iso:{isoPath}" };
            var result = await MakeMkv.RunAsync(makeMkvPath, args, log, progress: null, token);

            if (result.ExitCode != 0)
                throw MakeMkv.ExitFailure(result);

            var titles = Parse(result.Lines);
            if (titles.Count == 0)
                throw new MakeMkvException("MakeMKV listed no titles.");

            var problems = new List<string>();
            var skipped  = ParseSkipped(result.Lines, problems);
            problems.AddRange(Apply(record, titles, skipped));

            log.WriteLine();
            foreach (var problem in problems)
            {
                log.WriteLine(problem);
                progress.Report($"skipped: {problem}");
            }
            log.WriteLine($"Info complete: {titles.Count} title(s) listed, " +
                          $"{record.Titles.Count(t => t.RipName != null)} of {record.Titles.Count} joined");
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

    // Reads the TINFO lines into one entry per row. A row whose field 24
    // is not a number names no DVD title and is left out, as the oracle
    // does.
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

        var titles = new List<InfoTitle>();
        foreach (var (row, values) in rows)
        {
            if (!int.TryParse(Get(values, 24).Trim(), out var source)) continue;

            var duration = Get(values, 9);
            titles.Add(new InfoTitle(
                Row:         row,
                SourceTitle: source,
                Chapters:    int.TryParse(Get(values, 8), out var chapters) ? chapters : 0,
                Duration:    duration,
                Seconds:     Seconds(duration),
                SourceBytes: Bytes(Get(values, 11)),
                Segments:    Get(values, 26),
                RipName:     Get(values, 27)));
        }
        return titles;
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

    // Fills each record title from the row for its title number, then
    // runs the chapter gate and the play-all check. A title MakeMKV skipped
    // as a copy gets SameAs instead of a row. Returns a line for every
    // title or row that could not be joined, and every skipped title.
    private static List<string> Apply(DiscRecord record, List<InfoTitle> titles, Dictionary<int, int> skipped)
    {
        var problems = new List<string>();
        var bySource = new Dictionary<int, InfoTitle>();
        foreach (var info in titles)
            if (!bySource.TryAdd(info.SourceTitle, info))
                throw new MakeMkvException($"MakeMKV lists title {info.SourceTitle} more than once.");

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

            title.Duration    = info.Duration;
            title.Seconds     = info.Seconds;
            title.SourceBytes = info.SourceBytes;
            title.Segments    = info.Segments;
            title.RipName     = info.RipName;
            title.Verified    = info.Chapters == title.Chapters || info.Chapters == title.Chapters - 1;
            if (!title.Verified)
                title.Note = $"chapter mismatch: disc says {title.Chapters}, MakeMKV says {info.Chapters}";
        }

        FlagPlayAll(record);

        var known = record.Titles.Select(t => t.Number).ToHashSet();
        foreach (var info in titles.Where(t => !known.Contains(t.SourceTitle)))
            problems.Add($"MakeMKV row {info.Row} comes from title {info.SourceTitle}, " +
                         "which is not in the disc's title table");
        foreach (var (copy, kept) in skipped.Where(s => !known.Contains(s.Key)))
            problems.Add($"MakeMKV says it skipped title {copy} as the same as title {kept}, " +
                         "but that title is not in the disc's title table");
        foreach (var copy in skipped.Keys.Where(bySource.ContainsKey))
            problems.Add($"MakeMKV says it skipped title {copy}, but it also lists a row for it; the row is used");

        return problems;
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
                title.Note = $"play-all: same runtime as titles {string.Join("+", title.CompositeOf)}";
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
