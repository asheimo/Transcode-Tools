// ============================================================
// Ifo.cs
// ------------------------------------------------------------
// The DVD .IFO structures: the global title table (TT_SRPT) and
// the menu program chains (PGCI_UT) with their cells.
//
// Pure functions over bytes. Nothing here opens a file, touches
// a drive or reports progress -- it is handed an IFO's contents
// and returns what they say. That boundary is what lets the same
// parse be compared against the Python oracle and be unit tested
// without a disc in the machine.
//
// Offsets follow libdvdread's ifo_types.h, by way of the oracle
// (tools\oracle\dvdmenumap.py, class Ifo) which is proven on real
// discs. Do not adjust one from memory: fetch the source.
//
// Deliberately NOT here yet:
//   - PGC command tables (pgc + 0xE4) and the VM decode  -> seam 2
//   - the PGC chain resolver                             -> seam 3
// ============================================================

namespace TranscodeTools;

// Thrown when the bytes are not a DVD IFO, or an offset inside one
// points outside the file. Both mean the same thing to the caller:
// this IFO cannot be trusted, say so and move to the next one.
public sealed class IfoFormatException : Exception
{
    public IfoFormatException(string message) : base(message) { }
}

public enum IfoKind { Vmg, Vts }

// One row of the disc's global title table (TT_SRPT).
public sealed class IfoTitle
{
    public int Number   { get; init; }   // global title number, 1-based
    public int Angles   { get; init; }
    public int Chapters { get; init; }   // nr_of_ptts
    public int Vts      { get; init; }   // which title set holds it
    public int VtsTitle { get; init; }   // its number inside that set
}

// One cell of a menu program chain. A menu screen is a cell: its
// sector range is where the picture and the button data live.
public sealed class IfoCell
{
    public int  Number      { get; init; }   // 1-based within the PGC
    public int  StillTime   { get; init; }   // 0xFF = still until the viewer presses something
    public int  CellCommand { get; init; }   // index into the PGC's cell command table, 0 = none
    public long FirstSector { get; init; }
    public long LastSector  { get; init; }
}

// One menu program chain out of a language unit of PGCI_UT.
public sealed class IfoMenuPgc
{
    public string Lang     { get; init; } = "";   // two-letter code, e.g. "en"
    public bool   IsEntry  { get; init; }         // entry point for its menu type
    public int    EntryId  { get; init; }
    public int    MenuId   { get; init; }         // low nibble of EntryId
    public string Menu     { get; init; } = "";   // "root", "title", "chapter"...
    public int    Pgcn     { get; init; }         // 1-based within the language unit
    public int    Programs { get; init; }
    public int    StillTime { get; init; }
    public int    NextPgc  { get; init; }
    public int    PrevPgc  { get; init; }
    public int    GoUpPgc  { get; init; }
    public IReadOnlyList<IfoCell> Cells { get; init; } = Array.Empty<IfoCell>();
}

public sealed class Ifo
{
    public const int SectorSize = 2048;

    // Menu type by the low nibble of the PGC's entry id. 7 is the PTT
    // menu, which is what a disc calls scene selection.
    private static readonly Dictionary<int, string> MenuNames = new()
    {
        [2] = "title", [3] = "root", [4] = "subpicture",
        [5] = "audio", [6] = "angle", [7] = "chapter",
    };

    private readonly byte[] _data;

    public string  FileName { get; }
    public IfoKind Kind     { get; }

    // Sector where this IFO's menu VOB starts. Not used until frame
    // extraction (seam 4); kept because it is read from the same header.
    public long MenuVobsSector { get; }

    private readonly long _ttSrpt;    // sector, VMG only
    private readonly long _pgciUt;    // sector

    private Ifo(byte[] data, string fileName)
    {
        _data    = data;
        FileName = fileName;

        var magic = Ascii(0, 12);
        if (magic == "DVDVIDEO-VMG")
        {
            Kind           = IfoKind.Vmg;
            MenuVobsSector = U32(0xC0);
            _ttSrpt        = U32(0xC4);
            _pgciUt        = U32(0xC8);
        }
        else if (magic == "DVDVIDEO-VTS")
        {
            Kind           = IfoKind.Vts;
            MenuVobsSector = U32(0xC0);
            _ttSrpt        = 0;
            _pgciUt        = U32(0xD0);   // VTSM_PGCI_UT, not the VMG offset
        }
        else
        {
            throw new IfoFormatException($"{fileName}: not a DVD IFO (starts \"{magic}\")");
        }
    }

    public static Ifo Parse(byte[] data, string fileName)
    {
        // 0xD0 + 4 is the last header field either kind reads.
        if (data.Length < 0xD4)
            throw new IfoFormatException($"{fileName}: only {data.Length} bytes, too short to be an IFO");
        return new Ifo(data, fileName);
    }

    // ── Global title table (VMG only) ────────────────────────────────

    public IReadOnlyList<IfoTitle> Titles()
    {
        if (Kind != IfoKind.Vmg || _ttSrpt == 0) return Array.Empty<IfoTitle>();

        long baseOff = _ttSrpt * SectorSize;
        int  count   = U16(baseOff);

        var titles = new List<IfoTitle>(count);
        for (int i = 0; i < count; i++)
        {
            long o = baseOff + 8 + i * 12;
            titles.Add(new IfoTitle
            {
                Number   = i + 1,
                Angles   = U8(o + 1),
                Chapters = U16(o + 2),
                Vts      = U8(o + 6),
                VtsTitle = U8(o + 7),
            });
        }
        return titles;
    }

    // ── Menu program chains ──────────────────────────────────────────
    // PGCI_UT is a table of language units; each holds the menu PGCs for
    // one language. Every PGC is kept, including ones with no cells --
    // a cell-less PGC is usually a chain of commands that jumps somewhere
    // else, and the resolver (seam 3) needs it.

    public IReadOnlyList<IfoMenuPgc> MenuPgcs()
    {
        if (_pgciUt == 0) return Array.Empty<IfoMenuPgc>();

        long baseOff = _pgciUt * SectorSize;
        int  units   = U16(baseOff);

        var pgcs = new List<IfoMenuPgc>();
        for (int i = 0; i < units; i++)
        {
            long unitOff = baseOff + 8 + i * 8;
            string lang  = Ascii(unitOff, 2);
            long   unit  = baseOff + U32(unitOff + 4);
            int    count = U16(unit);

            for (int j = 0; j < count; j++)
            {
                long searchOff = unit + 8 + j * 8;
                int  entryId   = U8(searchOff);
                long pgc       = unit + U32(searchOff + 4);
                int  menuId    = entryId & 0x0F;

                pgcs.Add(new IfoMenuPgc
                {
                    Lang      = lang,
                    IsEntry   = (entryId & 0x80) != 0,
                    EntryId   = entryId,
                    MenuId    = menuId,
                    Menu      = MenuNames.TryGetValue(menuId, out var name) ? name : $"menu{menuId}",
                    Pgcn      = j + 1,
                    Programs  = U8(pgc + 2),
                    StillTime = U8(pgc + 0xA3),
                    NextPgc   = U16(pgc + 0x9C),
                    PrevPgc   = U16(pgc + 0x9E),
                    GoUpPgc   = U16(pgc + 0xA0),
                    Cells     = Cells(pgc),
                });
            }
        }
        return pgcs;
    }

    private IReadOnlyList<IfoCell> Cells(long pgc)
    {
        int  count  = U8(pgc + 3);
        int  cpbOff = U16(pgc + 0xE8);          // cell playback table, relative to the PGC
        if (count == 0 || cpbOff == 0) return Array.Empty<IfoCell>();

        var cells = new List<IfoCell>(count);
        for (int i = 0; i < count; i++)
        {
            long o = pgc + cpbOff + i * 24;
            cells.Add(new IfoCell
            {
                Number      = i + 1,
                StillTime   = U8(o + 2),
                CellCommand = U8(o + 3),
                FirstSector = U32(o + 8),
                LastSector  = U32(o + 20),
            });
        }
        return cells;
    }

    // ── Bounds-checked primitives ────────────────────────────────────
    // Every read goes through these. An IFO whose offsets point past the
    // end of the file is a damaged IFO, and it must fail by name rather
    // than as an IndexOutOfRangeException from somewhere in the middle.

    private void Require(long offset, int length)
    {
        if (offset < 0 || offset + length > _data.Length)
            throw new IfoFormatException(
                $"{FileName}: offset {offset} is outside the file ({_data.Length} bytes)");
    }

    private int U8(long o)
    {
        Require(o, 1);
        return _data[o];
    }

    private int U16(long o)
    {
        Require(o, 2);
        return (_data[o] << 8) | _data[o + 1];
    }

    private long U32(long o)
    {
        Require(o, 4);
        return ((long)_data[o] << 24) | ((long)_data[o + 1] << 16) |
               ((long)_data[o + 2] << 8) | _data[o + 3];
    }

    private string Ascii(long o, int length)
    {
        Require(o, length);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = (char)_data[o + i];
        return new string(chars);
    }
}
