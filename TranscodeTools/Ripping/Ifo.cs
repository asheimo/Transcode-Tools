// ============================================================
// Ifo.cs
// ------------------------------------------------------------
// The DVD .IFO structures: the global title table (TT_SRPT), the
// menu program chains (PGCI_UT) with their cells, and for following
// a button past the title it starts: each title set's title program
// chains (VTS_PGCIT), its chapter table (VTS_PTT_SRPT), and the
// disc's first-play program chain (FP_PGC).
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
// Each PGC's command tables (pre, post and cell commands) are
// decoded with VmCommand. They are kept in memory for the resolver
// and not saved on the disc record.
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

// One program chain: a menu's, a title's, or the first-play chain.
public class IfoPgc
{
    public int Pgcn      { get; init; }   // 1-based within its table
    public int Programs  { get; init; }
    public int StillTime { get; init; }

    // The chain's playback time in whole seconds (pgc_t playback_time,
    // BCD hours, minutes, seconds; the frame count is dropped).
    public int Seconds   { get; init; }
    public int NextPgc   { get; init; }
    public int PrevPgc   { get; init; }
    public int GoUpPgc   { get; init; }

    // The cell each program starts at, one entry per program. A chapter
    // names a program, so this is how a chapter jump finds its cell.
    public IReadOnlyList<int> ProgramMap { get; init; } = Array.Empty<int>();

    public IReadOnlyList<IfoCell> Cells { get; init; } = Array.Empty<IfoCell>();
    public IfoCommands Commands { get; init; } = IfoCommands.Empty;
}

// One menu program chain out of a language unit of PGCI_UT.
public sealed class IfoMenuPgc : IfoPgc
{
    public string Lang    { get; init; } = "";   // two-letter code, e.g. "en"
    public bool   IsEntry { get; init; }         // entry point for its menu type
    public int    EntryId { get; init; }
    public int    MenuId  { get; init; }         // low nibble of EntryId
    public string Menu    { get; init; } = "";   // "root", "title", "chapter"...
}

// Where one chapter of a title starts: a title program chain and the
// program within it.
public sealed record IfoChapter(int Pgcn, int Program);

// A PGC's command table (pgc_command_tbl_t): pre commands run on entry,
// post commands on exit, cell commands when a cell names one.
public sealed class IfoCommands
{
    public static readonly IfoCommands Empty = new();

    public IReadOnlyList<VmCommand> Pre  { get; init; } = Array.Empty<VmCommand>();
    public IReadOnlyList<VmCommand> Post { get; init; } = Array.Empty<VmCommand>();
    public IReadOnlyList<VmCommand> Cell { get; init; } = Array.Empty<VmCommand>();
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
    private readonly long _firstPlay; // byte offset of FP_PGC, VMG only
    private readonly long _pttSrpt;   // sector of VTS_PTT_SRPT, VTS only
    private readonly long _pgcit;     // sector of VTS_PGCIT, VTS only

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
            _firstPlay     = U32(0x84);
        }
        else if (magic == "DVDVIDEO-VTS")
        {
            Kind           = IfoKind.Vts;
            MenuVobsSector = U32(0xC0);
            _ttSrpt        = 0;
            _pttSrpt       = U32(0xC8);
            _pgcit         = U32(0xCC);
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
                    Lang       = lang,
                    IsEntry    = (entryId & 0x80) != 0,
                    EntryId    = entryId,
                    MenuId     = menuId,
                    Menu       = MenuNames.TryGetValue(menuId, out var name) ? name : $"menu{menuId}",
                    Pgcn       = j + 1,
                    Programs   = U8(pgc + 2),
                    StillTime  = U8(pgc + 0xA3),
                    Seconds    = PlaybackSeconds(pgc),
                    NextPgc    = U16(pgc + 0x9C),
                    PrevPgc    = U16(pgc + 0x9E),
                    GoUpPgc    = U16(pgc + 0xA0),
                    ProgramMap = ProgramMap(pgc),
                    Cells      = Cells(pgc),
                    Commands   = Commands(pgc),
                });
            }
        }
        return pgcs;
    }

    // ── Title program chains and chapters (VTS only) ─────────────────
    // VTS_PGCIT (libdvdread ifo_read_pgcit): a count, then one 8-byte
    // search pointer per PGC whose last four bytes are the PGC's offset
    // from the start of the table.

    public IReadOnlyList<IfoPgc> TitlePgcs()
    {
        if (Kind != IfoKind.Vts || _pgcit == 0) return Array.Empty<IfoPgc>();

        long baseOff = _pgcit * SectorSize;
        int  count   = U16(baseOff);

        var pgcs = new List<IfoPgc>(count);
        for (int i = 0; i < count; i++)
            pgcs.Add(Pgc(baseOff + U32(baseOff + 8 + i * 8 + 4), i + 1));
        return pgcs;
    }

    // VTS_PTT_SRPT (libdvdread ifo_read_vts_ptt_srpt): one list per title
    // of the set, in title order, each chapter four bytes (PGC number,
    // program number). A title's list runs to the next title's offset,
    // the last one to the table's last byte.
    public IReadOnlyList<IReadOnlyList<IfoChapter>> Chapters()
    {
        if (Kind != IfoKind.Vts || _pttSrpt == 0) return Array.Empty<IReadOnlyList<IfoChapter>>();

        long baseOff = _pttSrpt * SectorSize;
        int  titles  = U16(baseOff);
        long lastEnd = U32(baseOff + 4) + 1;

        var offsets = new long[titles];
        for (int i = 0; i < titles; i++)
            offsets[i] = U32(baseOff + 8 + i * 4);

        var list = new List<IReadOnlyList<IfoChapter>>(titles);
        for (int i = 0; i < titles; i++)
        {
            long end   = i + 1 < titles ? offsets[i + 1] : lastEnd;
            long count = (end - offsets[i]) / 4;
            var chapters = new List<IfoChapter>((int)Math.Max(0, count));
            for (long k = 0; k < count; k++)
            {
                long o = baseOff + offsets[i] + k * 4;
                chapters.Add(new IfoChapter(U16(o), U16(o + 2)));
            }
            list.Add(chapters);
        }
        return list;
    }

    // ── First-play program chain (VMG only) ──────────────────────────
    // What a player runs when the disc is inserted, before any menu.

    public IfoPgc? FirstPlayPgc() =>
        Kind == IfoKind.Vmg && _firstPlay != 0 ? Pgc(_firstPlay, 1) : null;

    private IfoPgc Pgc(long pgc, int pgcn) => new()
    {
        Pgcn       = pgcn,
        Programs   = U8(pgc + 2),
        StillTime  = U8(pgc + 0xA3),
        Seconds    = PlaybackSeconds(pgc),
        NextPgc    = U16(pgc + 0x9C),
        PrevPgc    = U16(pgc + 0x9E),
        GoUpPgc    = U16(pgc + 0xA0),
        ProgramMap = ProgramMap(pgc),
        Cells      = Cells(pgc),
        Commands   = Commands(pgc),
    };

    private int PlaybackSeconds(long pgc)
    {
        static int Bcd(int b) => (b >> 4) * 10 + (b & 0x0F);
        return Bcd(U8(pgc + 4)) * 3600 + Bcd(U8(pgc + 5)) * 60 + Bcd(U8(pgc + 6));
    }

    // The program map (libdvdread pgc_program_map_t): one byte per program,
    // the cell that program starts at.
    private IReadOnlyList<int> ProgramMap(long pgc)
    {
        int count = U8(pgc + 2);
        int off   = U16(pgc + 0xE6);
        if (count == 0 || off == 0) return Array.Empty<int>();

        var map = new int[count];
        for (int i = 0; i < count; i++)
            map[i] = U8(pgc + off + i);
        return map;
    }

    // pgc_command_tbl_t (libdvdread ifo_types.h): nr_pre, nr_post,
    // nr_cell, last_byte, then the 8-byte commands in that order. A table
    // that does not fit inside its own last_byte, or claims more than the
    // spec's 128 commands, is treated as absent, as the oracle does.
    private IfoCommands Commands(long pgc)
    {
        int off = U16(pgc + 0xE4);
        if (off == 0) return IfoCommands.Empty;

        long tbl   = pgc + off;
        int  nPre  = U16(tbl);
        int  nPost = U16(tbl + 2);
        int  nCell = U16(tbl + 4);
        int  last  = U16(tbl + 6);
        int  total = nPre + nPost + nCell;
        if (total == 0 || total > 128 || 8 + total * 8 > last + 1) return IfoCommands.Empty;

        int k = 0;
        List<VmCommand> Read(int count)
        {
            var list = new List<VmCommand>(count);
            for (int i = 0; i < count; i++, k++)
            {
                long o = tbl + 8 + k * 8;
                Require(o, 8);
                list.Add(VmCommand.Decode(new ReadOnlySpan<byte>(_data, (int)o, 8)));
            }
            return list;
        }

        var pre  = Read(nPre);
        var post = Read(nPost);
        var cell = Read(nCell);
        return new IfoCommands { Pre = pre, Post = post, Cell = cell };
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
