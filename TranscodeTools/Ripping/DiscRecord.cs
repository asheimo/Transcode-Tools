// ============================================================
// DiscRecord.cs
// ------------------------------------------------------------
// The per-disc record: everything the rip and analysis steps
// learn about one disc. It is the ripping "config" -- saved as
// <Destination>\Discs\<DISC>\disc.json (see DiscStore) and
// reloaded on the next run, so it must stand on its own once
// the ISO is deleted.
//
// Field names follow the Python oracle (dvdmenumap.py) so the
// two can be compared field by field. Deliberately NOT here yet:
// the menu graph. Its shape comes from the resolver port; it is
// added then, and records saved before that simply lack it.
// ============================================================

namespace TranscodeTools;

public sealed class DiscRecord
{
    // Bumped when the record's shape changes in a way old records
    // need converting for. Records without the menu graph are still 1.
    public int SchemaVersion { get; set; } = 1;

    // Volume label, e.g. "THE_CENTENNIAL". Also the folder name under
    // Discs\, Rip\ and the ISO file name.
    public string Name { get; set; } = "";

    // Where the disc was read from: the drive or the mounted ISO.
    public string SourcePath { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    // Full path of this disc's rip folder, <Destination>\Rip\<DISC>.
    public string RipFolder { get; set; } = "";

    public List<TitleRecord>  Titles  { get; set; } = new();
    public List<ScreenRecord> Screens { get; set; } = new();
}

// One title from the disc's title table (TT_SRPT), plus what the rip
// and the binding step learned about it.
public sealed class TitleRecord
{
    public int Number   { get; set; }   // global title number, 1-based
    public int Vts      { get; set; }   // title set it lives in
    public int VtsTitle { get; set; }   // title number inside that set
    public int Angles   { get; set; }
    public int Chapters { get; set; }   // from TT_SRPT

    // From MakeMKV's info run
    public string Duration { get; set; } = "";   // "h:mm:ss" as MakeMKV prints it
    public int    Seconds  { get; set; }
    public string Segments { get; set; } = "";   // TINFO field 26, e.g. "1-5,6-9"

    // For a play-all title: the titles whose runtimes add up to this one.
    public List<int> CompositeOf { get; set; } = new();

    // Binding
    public string? FileName    { get; set; }  // actual file on disk
    public bool    Bound       { get; set; }  // key matched and size inside the band
    public bool    Verified    { get; set; }  // chapter gate passed
    public long    SourceBytes { get; set; }  // TINFO field 11: title size on the disc
    public long    FileBytes   { get; set; }  // written file's size, measured at bind time

    // Every menu button that names this title, in the order found.
    public List<NamedBySource> NamedBy { get; set; } = new();

    // Other places that jump to this title (e.g. "VMGM pgc1 pre"),
    // so a title never goes missing without saying where it is reached from.
    public List<string> ReachedFrom { get; set; } = new();

    public TitleFlags Flags { get; set; } = new();

    public string Note { get; set; } = "";

    // The name the user typed. Empty until named.
    public string Name { get; set; } = "";
}

public sealed class TitleFlags
{
    public bool PlayAll      { get; set; }
    public bool NoMenuButton { get; set; }
}

// Identifies one button on one screen: the IFO and menu program chain
// the screen belongs to, the screen's cell, which button set in that
// cell, and the button number within the set.
public sealed class NamedBySource
{
    public string Ifo       { get; set; } = "";
    public int    Pgc       { get; set; }
    public int    Cell      { get; set; }
    public int    ButtonSet { get; set; }   // 1-based
    public int    Button    { get; set; }   // 1-based
}

// One menu screen: a cell of a menu program chain.
public sealed class ScreenRecord
{
    public string Ifo    { get; set; } = "";   // e.g. "VTS_01_0.IFO"
    public string Domain { get; set; } = "";   // "vmg" or "vts"
    public string Lang   { get; set; } = "";
    public string Menu   { get; set; } = "";   // menu type, e.g. "root"
    public int    Pgc    { get; set; }
    public int    Cell   { get; set; }

    // Cached frame, file name relative to the disc's folder. Null when
    // no picture was extracted for this screen.
    public string? Image { get; set; }

    public long FirstSector { get; set; }
    public long LastSector  { get; set; }
    public int  NavPacks    { get; set; }

    // Distinct button sets seen across the cell's navigation packets.
    public List<ButtonSetRecord> ButtonSets { get; set; } = new();
}

public sealed class ButtonSetRecord
{
    public int Number { get; set; }   // 1-based, in the order found
    public List<ButtonRecord> Buttons { get; set; } = new();
}

public sealed class ButtonRecord
{
    public int Number { get; set; }   // 1-based

    // Highlight rectangle as corners, in the picture's own pixels.
    public int X0 { get; set; }
    public int Y0 { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }

    // Button numbers reached by the remote's arrow keys (0 = none).
    public int Up    { get; set; }
    public int Down  { get; set; }
    public int Left  { get; set; }
    public int Right { get; set; }

    public string CommandText { get; set; } = "";   // decoded, e.g. "LinkPGCN 9"
    public string Raw         { get; set; } = "";   // the 8 command bytes as hex

    public ResolvedTarget? Resolved { get; set; }
}

// Where a button ends up once its command chain is followed.
public sealed class ResolvedTarget
{
    // "title", "menu", "resume", "exit", "loop" or "unresolved"
    public string Kind { get; set; } = "";

    public int?    Title    { get; set; }
    public int?    Chapter  { get; set; }
    public int?    Vts      { get; set; }
    public int?    VtsTitle { get; set; }
    public int?    Chapters { get; set; }
    public int?    MenuPgc  { get; set; }
    public string? Domain   { get; set; }   // for "menu": "VMGM" or "VTS n"
    public string? Why      { get; set; }   // reason for menu/loop/unresolved/resume

    public List<string> Trace { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}
