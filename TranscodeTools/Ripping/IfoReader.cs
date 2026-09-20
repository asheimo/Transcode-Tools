// ============================================================
// IfoReader.cs
// ------------------------------------------------------------
// The I/O half of seam 1: find a disc's VIDEO_TS folder, list its
// IFO files in the order they should be read, and read one.
//
// IFO files are read whole through the filesystem. CSS scrambles
// VOB payload, not IFOs, so everything here works straight off an
// optical drive with no backup and no decryption.
//
// The sector reader for VOB payload is NOT here: its only caller
// is the NAV pack scan that reads button data, which is seam 2.
// Staging menu VOBs to local disk is likewise seam 4's, where
// frame extraction needs it.
// ============================================================

using System.IO;
using System.Text.RegularExpressions;

namespace TranscodeTools;

// Thrown when a source path holds no readable DVD structure. The
// message is shown to the user as the job's status, so it says what
// was looked for and where.
public sealed class DiscSourceException : Exception
{
    public DiscSourceException(string message) : base(message) { }
}

public static class IfoReader
{
    // VIDEO_TS.IFO is the VMG; VTS_nn_0.IFO is a title set's.
    private static readonly Regex IfoName =
        new(@"^(VIDEO_TS|VTS_\d\d_0)\.IFO$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string VideoTsFolderName = "VIDEO_TS";
    public const string VmgFileName       = "VIDEO_TS.IFO";

    // Accepts a drive root ("D:\"), a mounted ISO's root, or a VIDEO_TS
    // folder itself. Discs mount as VIDEO_TS or video_ts depending on the
    // filesystem, so the lookup is case-insensitive rather than trusting
    // the name's case.
    public static string FindVideoTs(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new DiscSourceException("No source path was given.");

        if (!Directory.Exists(sourcePath))
            throw new DiscSourceException($"{sourcePath} could not be read. Is there a disc in the drive?");

        var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(trimmed), VideoTsFolderName, StringComparison.OrdinalIgnoreCase))
            return sourcePath;

        var child = FindCaseInsensitive(sourcePath, VideoTsFolderName, directories: true);
        if (child != null) return child;

        throw new DiscSourceException($"No {VideoTsFolderName} folder on {sourcePath}. Is this a DVD?");
    }

    // VMG first, then the title sets in name order -- the VMG carries the
    // title table every later IFO's titles are numbered against.
    public static IReadOnlyList<string> ListIfoFiles(string videoTs)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(videoTs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiscSourceException($"{videoTs} could not be listed: {ex.Message}");
        }

        var ifos = files
            .Where(f => IfoName.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => !string.Equals(Path.GetFileName(f), VmgFileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ifos.Count == 0)
            throw new DiscSourceException($"No IFO files in {videoTs}.");

        return ifos;
    }

    // A single unreadable IFO is reported and skipped by the caller, so
    // the read failure is surfaced as an exception with the file named.
    public static byte[] ReadIfo(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiscSourceException($"{Path.GetFileName(path)} could not be read: {ex.Message}");
        }
    }

    private static string? FindCaseInsensitive(string folder, string name, bool directories)
    {
        try
        {
            var entries = directories ? Directory.GetDirectories(folder) : Directory.GetFiles(folder);
            return entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
