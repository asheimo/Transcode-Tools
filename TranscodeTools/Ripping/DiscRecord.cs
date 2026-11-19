// ============================================================
// DiscRecord.cs
// ------------------------------------------------------------
// The per-disc record: everything the info stage and naming learn
// about one disc. It is the ripping "config" -- saved as
// <Destination>\Rip\<DISC>\Menu\disc.json (see DiscStore) and
// reloaded on the next run, the way Remux and Transcode keep their
// settings files. Apply in the title list saves the names to it.
//
// A DVD's titles come from its title table (the IFO), filled in by
// MakeMKV's info run; a Blu-ray's titles come from MakeMKV's info
// run alone, one per title it lists, keyed by the playlist it was
// built from. Records saved before this layout carry extra fields
// (menu screens, button targets); they are ignored on load.
// ============================================================

using System.Text.Json.Serialization;

namespace TranscodeTools;

public sealed class DiscRecord
{
    // Bumped when the record's shape changes in a way old records
    // need converting for.
    public int SchemaVersion { get; set; } = 1;

    // Volume label, e.g. "THE_CENTENNIAL". Also the folder name under
    // Rip\ and the backup's name.
    public string Name { get; set; } = "";

    // The backup the record was read from.
    public string SourcePath { get; set; } = "";

    // A Blu-ray (its backup is a BDMV folder); otherwise a DVD.
    public bool BluRay { get; set; }

    public DateTime CreatedUtc { get; set; }

    // Full path of this disc's rip folder, <Destination>\Rip\<DISC>.
    // Not saved: it is the folder the record was found in, set by
    // DiscStore on load and save, so moving the folder cannot leave a
    // stale path in the record.
    [JsonIgnore]
    public string RipFolder { get; set; } = "";

    // The log folder of the disc's last run, Logs\<timestamp>\<DISC>\,
    // holding its info.log. Naming and the player add their logs here.
    public string? RunLogFolder { get; set; }

    // Every .mkv the last finished rip wrote. Null until a rip finishes.
    public List<RipFileRecord>? RipFiles { get; set; }

    // Set when the disc is marked complete, cleared by Edit. Only this
    // makes a disc Complete; the app never infers it.
    public bool Complete { get; set; }

    public List<TitleRecord> Titles { get; set; } = new();
}

// One title: from the DVD's title table, or one MakeMKV lists on a
// Blu-ray, plus what MakeMKV's info run and naming add.
public sealed class TitleRecord
{
    // DVD: the global title number from TT_SRPT, which is the number VLC
    // gives a pick. Blu-ray: MakeMKV's title number plus one.
    public int Number   { get; set; }
    public int Vts      { get; set; }   // DVD: title set it lives in
    public int VtsTitle { get; set; }   // DVD: title number inside that set
    public int Angles   { get; set; }

    // The disc's own chapter count: TT_SRPT on a DVD, the playlist's
    // chapters by mkvmerge on a Blu-ray. 0 when there is none to read.
    public int Chapters { get; set; }

    // From MakeMKV's info run
    public int?   MakeMkvTitle { get; set; }        // MakeMKV's own title number, what `mkv` takes
    public string? Playlist    { get; set; }        // TINFO field 16, e.g. "00800.mpls" or "50201.m2ts"
    public string Duration     { get; set; } = "";  // "h:mm:ss" as MakeMKV prints it
    public int    Seconds      { get; set; }
    public string Segments     { get; set; } = "";  // TINFO field 26, e.g. "1-5,6-9"
    public long   SourceBytes  { get; set; }        // TINFO field 11: title size on the disc
    public string? RipName     { get; set; }        // TINFO field 27: the name MakeMKV gives this title's rip

    // The chapter check passed: MakeMKV's count against the disc's own.
    public bool Verified { get; set; }

    // For a play-all title: the titles whose runtimes add up to this one.
    public List<int> CompositeOf { get; set; } = new();

    // Set when MakeMKV's info run skipped this title as a copy of another
    // (MSG 3027): the number of the title it kept in its place. A pick of
    // this title lands on the kept title's row. Null for a title MakeMKV
    // did not skip.
    public int? SameAs { get; set; }

    // Binding, once the title is ripped
    public string? FileName  { get; set; }  // actual file on disk
    public bool    Bound     { get; set; }  // key matched and size inside the band
    public long    FileBytes { get; set; }  // written file's size, measured at bind time

    public TitleFlags Flags { get; set; } = new();

    public string Note { get; set; } = "";

    // The name given in naming, without ".mkv", an extra's Plex suffix
    // included ("Making Of-featurette"). Empty until named.
    public string Name { get; set; } = "";
}

public sealed class TitleFlags
{
    public bool PlayAll { get; set; }
}

// One file the rip wrote.
public sealed class RipFileRecord
{
    public string Name    { get; set; } = "";   // current file name, with ".mkv"
    public long   Bytes   { get; set; }         // size when the rip wrote it
    public bool   Deleted { get; set; }         // deleted in the naming step
}
