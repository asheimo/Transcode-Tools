// ============================================================
// MenuScan.cs
// ------------------------------------------------------------
// The I/O half of seam 2: open an IFO's menu VOB (VIDEO_TS.VOB for
// the VMG, VTS_nn_0.VOB for a title set) and walk a menu cell's
// sectors, counting NAV packs and collecting the distinct button
// sets they carry.
//
// Differences from the oracle's scan_cell, both agreed 2026-09-21:
//   - Every cell is scanned in full. The oracle stopped after 8,000
//     sectors, a limit with no recorded reason, most likely from when
//     it read straight off the optical drive.
//   - No staging. The oracle copied menu VOBs to local disk first
//     because optical reads were slow and failed; the app reads the
//     mounted backup ISO, which is already on local disk.
//
// Cell sectors in the IFO count from the start of the menu VOB, so a
// sector number is an offset into that one file.
// ============================================================

using System.IO;
using System.Text;

namespace TranscodeTools;

public sealed record CellScan(
    IReadOnlyList<IReadOnlyList<NavButton>> ButtonSets,
    int NavPacks,
    string? Problem);

public sealed class MenuVob : IDisposable
{
    private readonly FileStream _file;

    public string FileName { get; }
    public long   Sectors  { get; }

    private MenuVob(FileStream file, string fileName)
    {
        _file    = file;
        FileName = fileName;
        Sectors  = file.Length / NavPack.SectorSize;
    }

    // The menu VOB for an IFO, or null when the disc has none (a title set
    // with no menus) or it is empty. Found case-insensitively, as discs
    // mount as VIDEO_TS or video_ts.
    public static MenuVob? Open(string videoTs, string ifoFileName)
    {
        var stem    = Path.GetFileNameWithoutExtension(ifoFileName);
        var vobName = stem + ".VOB";

        var path = Directory.GetFiles(videoTs)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), vobName, StringComparison.OrdinalIgnoreCase));
        if (path == null) return null;

        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                  bufferSize: 64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiscSourceException($"{Path.GetFileName(path)} could not be opened: {ex.Message}");
        }

        if (file.Length < NavPack.SectorSize)
        {
            file.Dispose();
            return null;
        }
        return new MenuVob(file, Path.GetFileName(path));
    }

    // Walks sectors first..last of one cell. A cell reaching past the end
    // of the VOB is clipped to it; one starting past the end is not read,
    // and says so in Problem, as is a read error part way through (the
    // sets found before it are kept).
    public CellScan ScanCell(long first, long last, CancellationToken token)
    {
        var sets = new List<IReadOnlyList<NavButton>>();
        var seen = new HashSet<string>();
        int navs = 0;

        if (first >= Sectors)
            return new CellScan(sets, 0, $"{FileName}: cell starts at sector {first}, past the end of the file ({Sectors} sectors)");

        last = Math.Min(last, Sectors - 1);
        var buffer = new byte[NavPack.SectorSize];

        try
        {
            _file.Seek(first * NavPack.SectorSize, SeekOrigin.Begin);
            for (long sector = first; sector <= last; sector++)
            {
                if ((sector - first) % 1024 == 0) token.ThrowIfCancellationRequested();

                _file.ReadExactly(buffer);

                if (!NavPack.IsNavPack(buffer)) continue;
                navs++;

                var buttons = NavPack.ParseButtons(buffer);
                if (buttons == null) continue;

                // A set is the same set when every button's number, command
                // bytes and rectangle match; later NAV packs usually repeat it.
                if (seen.Add(SetKey(buttons)))
                    sets.Add(buttons);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            return new CellScan(sets, navs, $"{FileName}: read failed in sectors {first}-{last}: {ex.Message}");
        }

        return new CellScan(sets, navs, null);
    }

    // Copies `count` sectors starting at `first` into `destination`, stopping
    // early at the end of the file. Used to cut a menu cell out for ffmpeg.
    public void CopySectors(long first, long count, Stream destination)
    {
        count = Math.Min(count, Sectors - first);
        if (count <= 0) return;

        var buffer = new byte[NavPack.SectorSize * 64];
        _file.Seek(first * NavPack.SectorSize, SeekOrigin.Begin);
        long remaining = count * NavPack.SectorSize;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            _file.ReadExactly(buffer, 0, want);
            destination.Write(buffer, 0, want);
            remaining -= want;
        }
    }

    private static string SetKey(IReadOnlyList<NavButton> buttons)
    {
        var key = new StringBuilder();
        foreach (var b in buttons)
            key.Append(b.Number).Append(':').Append(b.Command.Raw).Append(':')
               .Append(b.X0).Append(',').Append(b.Y0).Append(',').Append(b.X1).Append(',').Append(b.Y1).Append(';');
        return key.ToString();
    }

    public void Dispose() => _file.Dispose();
}
