// ============================================================
// IfoReader.cs
// ------------------------------------------------------------
// The I/O half of the DVD title read: find a disc's VIDEO_TS folder
// and read its VIDEO_TS.IFO, the file holding the title table (Ifo).
//
// IFO files are read whole through the filesystem. CSS scrambles
// VOB payload, not IFOs, so this works on a mounted backup with no
// decryption.
// ============================================================

using System.IO;

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

    // The bytes of the VIDEO_TS.IFO in `videoTs`.
    public static byte[] ReadVmg(string videoTs)
    {
        var path = FindCaseInsensitive(videoTs, VmgFileName, directories: false)
            ?? throw new DiscSourceException($"No {VmgFileName} in {videoTs}.");
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiscSourceException($"{VmgFileName} could not be read: {ex.Message}");
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
