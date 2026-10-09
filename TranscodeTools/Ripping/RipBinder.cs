// ============================================================
// RipBinder.cs
// ------------------------------------------------------------
// Port seam 6: ties each disc title to its ripped file, after the
// rip has written them into Rip\<DISC>\. A port of the oracle's
// match_rips + the binding half of join_titles.
//
// The key is the name MakeMKV gave the title in the info run (TINFO
// field 27, RipName). The guard is size: the file must come out at
// 0.95-1.00 of the title's size on the disc (TINFO field 11;
// measured 0.978-0.981). The guard is what catches an info run and
// a rip that disagree -- the names still line up, but with the wrong
// titles, and the sizes fall out of the band. A title that failed
// the chapter gate in the info stage is never bound.
//
// Every .mkv left over that no title claims gets "_unmatched_" put
// in front of its name on disk, so it can't be mistaken for a named
// rip. Files that already carry it are left alone, so running the
// binder again changes nothing.
// ============================================================

using System.IO;

namespace TranscodeTools;

public static class RipBinder
{
    public const string UnmatchedPrefix = "_unmatched_";

    private const double SizeBandLow  = 0.95;
    private const double SizeBandHigh = 1.00;

    // Binds the record's titles to the .mkv files in `ripFolder` and marks
    // the rest unmatched. Returns one line per title and per renamed file,
    // for the rip log. Throws IOException / UnauthorizedAccessException if
    // an unmatched file can't be renamed.
    public static List<string> Bind(DiscRecord record, string ripFolder)
    {
        var lines = new List<string>();
        var files = Directory.GetFiles(ripFolder, "*.mkv")
                             .ToDictionary(f => Path.GetFileName(f), f => new FileInfo(f).Length,
                                           StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var title in record.Titles)
        {
            title.Bound     = false;
            title.FileName  = null;
            title.FileBytes = 0;

            var reason = Check(title, files);
            if (reason != null)
            {
                title.Note = title.Note.Length == 0 ? reason : $"{title.Note}; {reason}";
                lines.Add($"title {title.Number}: not bound: {reason}");
                continue;
            }

            title.Bound     = true;
            title.FileName  = title.RipName;
            title.FileBytes = files[title.RipName!];
            claimed.Add(title.RipName!);
            lines.Add($"title {title.Number}: bound to {title.FileName} " +
                      $"({(double)title.FileBytes / title.SourceBytes:0.000} of the disc title)");
        }

        foreach (var name in files.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (claimed.Contains(name) || name.StartsWith(UnmatchedPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            File.Move(Path.Combine(ripFolder, name), Path.Combine(ripFolder, UnmatchedPrefix + name));
            lines.Add($"{name}: no title claims it, renamed {UnmatchedPrefix}{name}");
        }

        return lines;
    }

    // Why a title can't be bound, or null when it can.
    private static string? Check(TitleRecord title, Dictionary<string, long> files)
    {
        if (string.IsNullOrEmpty(title.RipName))
            return "MakeMKV listed no file for it";
        if (!title.Verified)
            return "it failed the chapter check";
        if (!files.TryGetValue(title.RipName, out var size))
            return $"no file named {title.RipName} in the rip folder";
        if (title.SourceBytes <= 0)
            return "MakeMKV gave no size for it";

        var ratio = (double)size / title.SourceBytes;
        if (ratio < SizeBandLow || ratio > SizeBandHigh)
            return $"{title.RipName} is {ratio:0.000} of the disc title's {title.SourceBytes:N0} bytes, " +
                   $"outside {SizeBandLow:0.00}-{SizeBandHigh:0.00}: the rip and the info run disagree";
        return null;
    }
}
