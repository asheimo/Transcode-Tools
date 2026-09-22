// ============================================================
// DiskSpace.cs
// ------------------------------------------------------------
// Rip mode's disk-space rule for backups:
//
//   - Checked when Backup is clicked: free space on the Destination
//     must cover ReservationFactor x the size Windows reports for the
//     disc. MEASURED: that size is the finished ISO minus one 2,048-byte
//     sector, and the ISO grows as MakeMKV writes it (0 during the scan,
//     then 32 MiB steps).
//
//   - Held while the backup runs: a file in <Destination>\Reserve\ is
//     given that length up front, which on NTFS claims the disk space at
//     once without writing any data. As the ISO grows the reserve file
//     is shortened by the same amount, which hands the space back, so
//     reserve + ISO stays at the reservation until the backup ends.
//     The file is opened delete-on-close: success, failure or cancel,
//     closing it frees whatever is left.
//
//   - Because held space is really held, Windows' own free-space figure
//     already accounts for every running backup. No running totals are
//     kept.
//
//   - A power cut leaves a reserve file behind, so Reserve\ is swept
//     when a Destination loads. A file a running backup still holds
//     cannot be deleted, so the sweep skips it on its own.
// ============================================================

using System.IO;
using System.Runtime.InteropServices;

namespace TranscodeTools;

public static class DiskSpace
{
    public const double ReservationFactor = 1.25;
    public const string ReserveFolderName = "Reserve";

    public static string ReserveFolder(string destination) =>
        Path.Combine(destination, ReserveFolderName);

    public static long ReservationFor(long discBytes) =>
        (long)Math.Ceiling(discBytes * ReservationFactor);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    // Free space available to this user on the volume holding `path`.
    // GetDiskFreeSpaceEx rather than DriveInfo, because it also accepts
    // a network path (\\server\share\...) as the Destination.
    // Throws Win32Exception with Windows' own message when it can't be read.
    public static long FreeBytes(string path)
    {
        var folder = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        if (!GetDiskFreeSpaceEx(folder, out var available, out _, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return (long)Math.Min(available, long.MaxValue);
    }

    // Deletes leftover reserve files. Files still open by a running
    // backup refuse deletion and are left alone.
    public static void SweepReserve(string destination)
    {
        var folder = ReserveFolder(destination);
        if (!Directory.Exists(folder)) return;

        string[] files;
        try { files = Directory.GetFiles(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var file in files)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

// The space one running backup holds. Created at the full reservation;
// Shrink hands back space as the ISO grows; Dispose frees the rest.
public sealed class SpaceReservation : IDisposable
{
    private readonly object     _gate = new();
    private readonly long       _reserved;
    private FileStream?         _file;

    public string Path { get; }

    private SpaceReservation(string path, FileStream file, long reserved)
    {
        Path      = path;
        _file     = file;
        _reserved = reserved;
    }

    // Throws IOException (for example when the disk has less room than
    // `bytes` by the time the file is sized) or UnauthorizedAccessException.
    public static SpaceReservation Create(string destination, string discName, long bytes)
    {
        var folder = DiskSpace.ReserveFolder(destination);
        var dir    = Directory.CreateDirectory(folder);
        if (!dir.Attributes.HasFlag(FileAttributes.Hidden))
            dir.Attributes |= FileAttributes.Hidden;

        var path = System.IO.Path.Combine(folder, discName + ".reserve");
        var file = new FileStream(path, new FileStreamOptions
        {
            Mode    = FileMode.Create,
            Access  = FileAccess.Write,
            Share   = FileShare.None,
            Options = FileOptions.DeleteOnClose,
        });

        try
        {
            file.SetLength(bytes);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        return new SpaceReservation(path, file, bytes);
    }

    // Shortens the reserve file to what the backup has not yet written.
    // Only ever shrinks: an ISO that outgrows the reservation leaves the
    // file at zero and carries on into ordinary free space.
    public void Shrink(long bytesWritten)
    {
        lock (_gate)
        {
            if (_file == null) return;
            var target = Math.Max(0, _reserved - bytesWritten);
            if (target < _file.Length)
                _file.SetLength(target);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
