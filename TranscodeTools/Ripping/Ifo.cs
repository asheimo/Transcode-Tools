// ============================================================
// Ifo.cs
// ------------------------------------------------------------
// The DVD's global title table (TT_SRPT) from VIDEO_TS.IFO: one
// entry per title, with its chapter count, angles and the title set
// that holds it. The info stage checks MakeMKV's chapter count for
// each title against this table (MakeMkvInfo).
//
// A pure function over bytes. Nothing here opens a file, touches a
// drive or reports progress -- it is handed the IFO's contents and
// returns what they say.
//
// Offsets follow libdvdread's ifo_types.h. Do not adjust one from
// memory: fetch the source.
// ============================================================

namespace TranscodeTools;

// Thrown when the bytes are not a DVD VMG IFO, or an offset inside it
// points outside the file.
public sealed class IfoFormatException : Exception
{
    public IfoFormatException(string message) : base(message) { }
}

public sealed class IfoTitle
{
    public int Number   { get; init; }   // global title number, 1-based
    public int Angles   { get; init; }
    public int Chapters { get; init; }   // nr_of_ptts
    public int Vts      { get; init; }   // which title set holds it
    public int VtsTitle { get; init; }   // its number inside that set
}

public static class Ifo
{
    private const int    SectorSize    = 2048;
    private const string VmgMagic      = "DVDVIDEO-VMG";
    private const int    TtSrptPointer = 0xC4;   // sector of TT_SRPT in the VMG

    // The title table of a VIDEO_TS.IFO. Throws IfoFormatException when
    // the bytes are not a VMG or the table runs past the end of the file.
    public static IReadOnlyList<IfoTitle> ReadTitles(byte[] data, string fileName)
    {
        if (data.Length < TtSrptPointer + 4)
            throw new IfoFormatException($"{fileName}: only {data.Length} bytes, too short to be an IFO");

        var magic = System.Text.Encoding.ASCII.GetString(data, 0, VmgMagic.Length);
        if (magic != VmgMagic)
            throw new IfoFormatException($"{fileName}: not a DVD VMG IFO (starts \"{magic}\")");

        long baseOff = U32(data, TtSrptPointer, fileName) * SectorSize;
        int  count   = U16(data, baseOff, fileName);

        var titles = new List<IfoTitle>(count);
        for (int i = 0; i < count; i++)
        {
            long o = baseOff + 8 + i * 12;
            titles.Add(new IfoTitle
            {
                Number   = i + 1,
                Angles   = U8(data, o + 1, fileName),
                Chapters = U16(data, o + 2, fileName),
                Vts      = U8(data, o + 6, fileName),
                VtsTitle = U8(data, o + 7, fileName),
            });
        }
        return titles;
    }

    // Every read is bounds-checked, so a damaged IFO fails by name rather
    // than with an IndexOutOfRangeException.
    private static void Require(byte[] data, long offset, int length, string fileName)
    {
        if (offset < 0 || offset + length > data.Length)
            throw new IfoFormatException($"{fileName}: offset {offset} is outside the file ({data.Length} bytes)");
    }

    private static int U8(byte[] data, long o, string fileName)
    {
        Require(data, o, 1, fileName);
        return data[o];
    }

    private static int U16(byte[] data, long o, string fileName)
    {
        Require(data, o, 2, fileName);
        return (data[o] << 8) | data[o + 1];
    }

    private static long U32(byte[] data, long o, string fileName)
    {
        Require(data, o, 4, fileName);
        return ((long)data[o] << 24) | ((long)data[o + 1] << 16) | ((long)data[o + 2] << 8) | data[o + 3];
    }
}
