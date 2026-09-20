# movie_namer — Session Handoff

**Date:** 2026-09-20 (supersedes 2026-09-18)
**Status:** port seam 1 is in and running. Pressing Start reads a disc's IFOs straight off the
drive, writes the record and fills the disc list. No other engine module is ported.
**Home:** TranscodeTools (WPF, C#) — `C:\Users\andy\source\repos\Transcode-Tools`, solution
`Transcode Tools.sln` at the repo root, project in `TranscodeTools\`.
**Branch:** `feature/ripping`, cut from `wpf-rewrite`. Files for this session were handed over
whole; committing them is Andy's.
**Reference only:** `tools\oracle\dvdmenumap.py` (~2240 lines) in the repo.

---

## What was built this session

### New, under `TranscodeTools\Ripping\`

- **`Ifo.cs`** — the IFO structures. Magic check, TT_SRPT at `0xC4` (VMG), PGCI_UT at `0xC8`
  (VMG) and `0xD0` (VTS), the language-unit walk over menu PGCs, and the cell playback table at
  PGC `+0xE8`. Every read goes through bounds-checked `U8/U16/U32`, so a bad offset fails as
  `VTS_03_0.IFO: offset N is outside the file` rather than an `IndexOutOfRangeException`. No
  I/O in the file at all — that is what lets it be compared against the oracle and tested
  without a disc.
- **`IfoReader.cs`** — finds `VIDEO_TS` case-insensitively from a drive root or a mounted ISO,
  lists the IFOs with the VMG first, reads one.
- **`RipJob.cs`** — the jobs-list row (`Disc`, `Step`, `Status`, `IsError`, `CancelVisibility`,
  `Cancel()`) and `DiscAnalysis.ReadDisc`, which fills the record. A malformed IFO is reported
  and skipped and the rest are read; nothing readable at all fails the job. Reads go into local
  lists before they touch the record, so a partial parse adds nothing.

### Changed

- **`DiscStore.cs`** — gained `TryLoad`, so the replace prompt can name what it is replacing.
- **`RipView.xaml`** — Cancel column on the jobs list, columns bound, Status width 230 so the
  four columns fit at the default window, horizontal splitter between the drives/jobs band and
  the row below, `GripSplitterH` template, local `JobErrorForeground` brush.
- **`RipView.xaml.cs`** — jobs collection, Start wired to the disc read, Cancel, one job per
  drive, the replace prompt, and every Start refusal reported rather than returning silently.

### Measured 2026-09-20

- Builds and runs. Start performs the analysis on a real disc.
- Two build failures on the way in, both mine: a `--` inside a XAML comment (the rules doc says
  not to; line 72), and `Path` ambiguous between `System.IO` and `System.Windows.Shapes` in
  `RipView.xaml.cs`, where the existing code already qualified it in full. A XAML file that
  will not parse takes the markup compiler down with it, which is why eight unrelated
  "does not exist in the namespace" errors appeared alongside each.
- `TranscodeTools.csproj` targets `net8.0-windows`. The reference doc still says .NET 9.

### Seam 1's boundary — narrower than the 09-18 plan

- `ScreenRecord.ButtonSets` stays **empty** and `NavPacks` stays **0**. A button's rectangle
  comes from the NAV pack but its `CommandText` comes from the VM decode, and a half-filled
  button set is indistinguishable on disk from a disc with no menu buttons. Counting NAV packs
  means the same VOB sector walk that reads the rectangles, so doing it now pays the I/O twice.
  Both arrive with seam 2.
- No VOB sector reader in `IfoReader.cs` and no staging: their only callers are seam 2 and seam
  4. `RipFolder` is left empty rather than pre-filled with a path the record could be moved out
  from under.

---

## Decided 2026-09-20

### Destination is not a disabling condition

A missing Destination no longer disables Start — a disabled button raises no click, so all it
could offer was a tooltip, and that is too quiet for the one mistake that stops everything.
Start stays live and shows a message box: **"Please set a destination folder."** One line; the
longer explanatory version was cut. The box does **not** offer to open the picker — "if we give
the user a way to fix it there they will never learn good habits."

This narrows the 09-18 shown-disabled-with-a-tooltip rule; it does not replace it. No disc and
already-running still disable Start, with the tooltip saying which.

### The drive row owns the backup

- The drive row's button becomes **Backup**.
- The row's background fills green with **real** MakeMKV progress — `--progress=-same` gives
  `PRGV:current,total,max`. "The info is available we should use it."
- On failure the row's background turns **red** and the button opens that disc's log.
- After a successful backup the button becomes **Eject**. Needs a `DeviceIoControl` /
  `IOCTL_STORAGE_EJECT_MEDIA` P/Invoke; there is no .NET call. May be revisited if auto mode is
  added.
- **Nothing appears in the jobs list until the backup completes.** The jobs list then carries
  the start/cancel button for the IFO stage.
- On a failed **job** row it is likewise the **background** that goes red; the text is
  unchanged. This corrects what shipped this session, which reddened the status text instead.
  Proposed: a translucent `#59D64545` laid over the row, which tints whatever surface is under
  it and keeps the theme foreground legible in both themes. A solid colour would need two new
  theme keys, and the theme is frozen. **Not yet implemented** — it rides with the backup port.

### Destination layout — Andy's tree

Supersedes the 09-18 `<Destination>\Discs\<DISC>\` decision and the two-settable-paths history.

```
<Destination>\
    ISO\
        THE_CENTENNIAL.iso
    Rip\
        THE_CENTENNIAL\
            C1_t00.mkv
            C1_t01.mkv
            Menu\
                disc.json              the disc record
                titles.txt             MakeMKV info output
                vts01_pgc9_c1.png      cached menu frames
    Reserve\                           hidden; one preallocated file per running job
    Logs\
        Backup\
            THE_CENTENNIAL.log         flat, one per disc
            SPEED.log
        20260920_143002\
            THE_CENTENNIAL\
                <log files>            names OPEN, see below
```

- **`Rip\<DISC>\` is the folder the user points remux at.** The record is therefore *meant* to
  die when that folder is deleted after remux. This reverses the earlier persistence note that
  had the record outliving the rips; that wording is wrong and should not be kept alongside
  this.
- `Logs\Backup\` is the surviving history of which discs have been processed.
- `Menu\` was renamed from `Info\` because Info is already a pipeline stage and a Step value.
- Consequence to handle when remux meets this: `Menu\` sits inside the folder remux reads, so
  the folder tree and auto-move need to skip it the way they already skip `Completed\`,
  `Logs\`, `Remux\` and `Transcode\`.
- The code currently writes `<Destination>\Discs\<DISC>\disc.json`. Moving it to
  `Rip\<DISC>\Menu\` lands with the backup port, and `RipFolder` stops being a stored field —
  it becomes the parent of the folder the record was found in.

### Starting a disc that has been seen before

Two prompts, keyed on what is actually on disk. Yes proceeds, No stops, nothing is deleted.

- ISO present → **"THE_CENTENNIAL already exists. Overwrite?"**
- ISO gone, backup log present → **"THE_CENTENNIAL has been processed before. Back it up
  again?"**

Kept short to match the app's other system messages. The `disc.json` check that shipped this
session is superseded by these: the record now dies with the rips, so after a cleanup it would
find nothing and start silently.

Andy's reasoning: a redo is either a retry after a failure, where no rips were made, or a
deliberate redo long after the rip folder went. Old .mkv files under a renamed name surviving a
second rip is a rare case and visible in the folder.

### The backup port is the next task

It goes ahead of seam 2. The IFO parse loses its own trigger once the drive button means
Backup, and backup blocks seam 4 regardless: VTS-domain menu frames do not render off the
optical drive and the episode index is VTS-domain.

- **MakeMKV path**: `MakeMKV_Path` in `AppSettings` beside the other six tool paths, plus a
  seventh row on the Preferences **Tool Paths** tab on the existing
  label / textbox / Where / Browse pattern. Empty by default like the rest; a backup refuses
  with a message naming the path rather than guessing at Program Files.
- **Space**: the rule goes in from the start — free-space check *and* the `Reserve\`
  preallocated delete-on-close file with the startup sweep. Flagged at the time: the check is
  two lines, the reserve file is nearer thirty.
- **Logs**: `Logs\Backup\<DISC>.log`, flat, one per disc, overwritten by a repeat.

---

## Open — pick up here

**Next task: write the backup port.**

Files:

- `AppSettings.cs` + `UserPreferences.xaml(.cs)` — `MakeMKV_Path` and its Tool Paths row.
- `Ripping\BackupJob.cs` — the `makemkvcon` wrapper, `PRGV` progress, the two prompts, the log
  file, cancel.
- `Ripping\DiskSpace.cs` — free-space check, `Reserve\` preallocation, startup sweep.
- `RipView.xaml(.cs)` — Backup/Eject button, green progress fill, red row with the log button,
  the eject P/Invoke, the record's move to `Rip\<DISC>\Menu\`, and the job row's red background.

**The one open design question:** the log file names inside `Logs\<timestamp>\<DISC>\`.

Andy drew `C1_t00.log` / `C1_t01.log`, one per ripped title. MEASURED: a successful
`makemkvcon mkv` run names no files and emits one stream of output for the whole rip, so
splitting it per title means attributing lines by `MSG:3028` index, and the failure messages
worth reading arrive with run-level context above them. Two ways to get what was drawn:

- Rip one title per MakeMKV run — each run has its own output and its own log named for the
  file it produced. MEASURED on THE_CENTENNIAL: `mkv iso:... 0 <dir>` works and still names the
  file `C1_t00.mkv`. Costs a process launch per title and reopens per-title invocation, cut
  earlier as overengineering when it was about output folders.
- One log per stage: `info.log`, `rip.log`, `naming.log`.

Claude leans to per-stage; the weaker half of the case is that per-title runs would give the
title→file mapping structurally rather than through the field-27 key.

**Open, not blocking:** what an expanded disc title row shows, including where the name is
typed. Settle once the view has real data.

**To measure on Andy's hardware:**

- What size Windows reports for a disc's drive letter before anything is read. The backup
  reservation (disc size × 2) depends on it.
- Whether MakeMKV's backup file grows on disk as it writes. Shrinking the reserve file depends
  on watching that growth; if the file only appears at the end, use the progress output.

**Known, not fixed:**

- A mounted ISO shows up in the drives box, because Windows reports it as an optical drive, and
  it gets a button. Needs a real-drive check before the app mounts its own ISOs.
- GridView stub columns: an empty header segment on the right of the drives, jobs and disc
  lists. Cosmetic.
- Cell text alignment sits slightly left of its header, same as the existing lists. Step 12.
- .NET version mismatch: csproj says `net8.0-windows`, the reference doc says .NET 9. One of
  them is wrong.

**Docs known stale:**

- `Development_Rules.md` still describes the zip workflow and v1/v2 zip naming. Both dead.
- `TranscodeTools_Reference.md` still describes the app as Remux and Transcode only.
- The repo `README.md` still says the transcode side wraps other-transcode, removed at Step 2.

*Queue, parked:*

- Auto-start checkbox (Backup on disc insert).
- Preferences panel: Rip/Remux/Transcode mode checkboxes, gated app locations, info/rip job cap.
- OCR pass: button-label OCR → keyword classification → screen ranking.
- Disc identity by fingerprint.
- Blu-ray contact-sheet generator.
- Help dialog.
- **Cheap check, never run:** `TXTDT_MGI` at VMG offset `0xD4` may hold real title text. Almost
  always empty on commercial discs; would beat OCR outright.

---

## The pipeline

```
backup → info → rip → naming
```

Unattended: backup, info, rip. Attended: naming only. Analysis rides at the tail of **rip**,
still unattended: `IFO parse → VM decode → resolver → frame extraction → binding`. Because rip
precedes naming, discs arrive in the naming view already bound.

The standalone IFO parse on the drive button is a **development shortcut**, not the design. One
job per disc moves Backup → Info → Rip → Analyse through the Step column; it is not a job per
stage. Whether, once the chain is joined, Start always runs the whole thing or reading the IFOs
alone survives as an option is a call for then.

### Module seams

1. IFO structures + sector reader + staging — **IFO half done**; sector reader and staging
   moved to 2 and 4
2. VM decode (libdvdnav `vm_print_mnemonic` port) — carries button sets and NavPacks
3. PGC chain resolver — defines the menu graph's shape on the record
4. Frame extraction — needs the backup; VTS frames do not render off the drive
5. MakeMKV `titles.txt` parser
6. Rip binder
7. Rename engine
8. Views — first pass in

The backup port is now ahead of 2. 1–3 stay pure functions over bytes with no I/O; if the port
loses that boundary it has moved the tangle rather than removed it. All modules live under
`Ripping\`.

### Reporting

Every module needs a status/progress channel in its signature. The Python prints to stdout;
ported literally that becomes output that vanishes. `RipJob` uses `IProgress<string>` created on
the UI thread.

### Acceptance

`--selftest` covers the decoder, resolver and rip matcher and does not survive the port for
free. The Python stays under `tools\oracle\`: run both against the same disc and compare. Field
names on `disc.json` follow the script's, but the layout differs, so the comparison maps fields,
not bytes. Python retires when the comparison is clean, not when the code compiles.
`THE_CENTENNIAL` has a known-good run on Andy's machine.

---

## Jobs and disk space

- **Backup:** one job per drive.
- **Info and rip: no default limit.** A preference caps the count.
- **Reservation:** each backup reserves disc size × 2 from the start — its ISO plus its rips.
  That is the enforcement. No resizing after the info run.
- **Accounting:** a job starts only if *free space now − what every running job still needs ≥
  what this job needs*, where *still needs* = reservation − bytes written so far. Checks run one
  at a time.
- **Space held on disk:** a reserve file in `Reserve\`, opened with
  `FileStreamOptions.PreallocationSize` and delete-on-close, shrinking as real output grows.
  Closed at job end, success or failure or cancel; a power cut leaves it behind, so `Reserve\`
  is swept at startup.
- **Short on space:** the job waits and its row shows the numbers. Refused only if the disc
  cannot fit with nothing else running.
- **Over the reservation:** a disc needing more than 2× carries on into ordinary free space; if
  that runs out it fails visibly.
- **Every failure is obvious.** A failed backup turns the drive row red; a failed rip keeps its
  ISO (delete gate).

---

## Standing

### Layout

```
User Mode:  (Rip) (Remux) (Transcode)
[Destination] [history dropdown..............................]   <- full width
drives box  [Backup]  ||  jobs in flight                         <- own splitter
--------------------------------------------------------------  <- own splitter (new 09-20)
left column (270, resizable) |  chip strip (fixed height)
  discs -> disc titles       |  menu frame
--------------------------------------------------------------
status
```

- A *disc title* is a title row on the disc, kept separate from the *name* typed for it.
- A disc title row is one line: number, runtime, filename. Chips are not on the row.
- Chip strip above the picture, one chip per source screen + button set, in the order found, no
  ranking. It always keeps its height so the picture never moves.
- Picture at its original stored size, square pixels, no 4:3 correction; shrink-only.
- Jobs list columns: Disc 180, Step 90, Status 230, Cancel 90. Fixed widths, so spare width
  falls into the stub column rather than into Status.

### Theme is frozen

`FlatButton` has no disabled state, so a disabled button looks clickable. Andy's decision: no
change, not to the theme and not to a Rip-only style. Do not reopen. This is why the failure
colour is a local brush rather than a theme key, and why a disabled button can never be the
only thing carrying a message.

### Record shape

```
DiscRecord      SchemaVersion (1), Name, SourcePath, CreatedUtc, RipFolder
                Titles[], Screens[]                        (MenuGraph: later)

TitleRecord     Number, Vts, VtsTitle, Angles, Chapters
                Duration, Seconds, Segments, CompositeOf[]
                FileName, Bound, Verified, SourceBytes, FileBytes
                NamedBy[] -> { Ifo, Pgc, Cell, ButtonSet, Button }
                ReachedFrom[], Flags -> PlayAll, NoMenuButton
                Note, Name

ScreenRecord    Ifo, Domain, Lang, Menu, Pgc, Cell
                Image, FirstSector, LastSector, NavPacks
                ButtonSets[] -> Number, Buttons[]

ButtonRecord    Number, X0, Y0, X1, Y1, Up, Down, Left, Right
                CommandText, Raw
                Resolved { Kind, Title, Chapter, Vts, VtsTitle, Chapters,
                           MenuPgc, Domain, Why, Trace[], Notes[] }
```

Saved with `System.Text.Json` to a `.tmp` and moved into place, so a crash cannot leave a
half-written record. Menu graph left off until the resolver defines its shape.

### Repo and branch

Public at `github.com/asheimo/Transcode-Tools`. Default branch `wpf-rewrite`, not `main`. Files
are pulled from GitHub directly; there are no push credentials on Claude's side, so changes come
back as whole files or a git bundle. One long-lived feature branch holds the whole
ripping/naming build. `docs\` and `tools\oracle\` sit at the repo root.

### Preview button

Kept, repurposed as a Plex naming-convention check, not a dry run — title case with the acronym
list, resolution suffix after the edition tag, missing-year detection.

### Typed names

The app renames the file on disk. Reopening a named disc shows the resulting filename in the
name field, not editable unless you right-click.

---

## Facts that must survive the port

### The join

- **Key = `TINFO` field 27** (output filename). It carries MakeMKV's title index.
- **Guard = `TINFO` field 11.** The file must land between **0.95 and 1.00** of it. Measured
  0.978–0.981 from 9.5 MB to 7.8 GB. Field 11 is the source title size on the disc, never the
  size of the file written.
- The guard catches a `--minlength` desync between the info run and the rip.
- **Not** the row index. **Not** the chapter count — MakeMKV strips the trailing menu-jump cell.
  Gate accepts `chapters == TT_SRPT` or `TT_SRPT - 1`.
- The oracle's `parse_makemkv_info` still keys on field 24 and its docstring says so; the
  handoff decision (field 27 key, field 11 guard) is the one to port.
- **Play-all detection from data alone:** multi-range segment map (field 26) plus runtime equal
  to the sum of the titles it spans (±2 s, combinations of 2–4). Flag, don't hide.
- Unmatched titles: the actual file on disk gets `_unmatched_` prepended. Idempotent.

### MakeMKV robot mode — measured

- A successful `mkv` run names no files. Per-title output is `MSG:3028`, then `MSG:5005 "N
  titles saved"`. Filenames appear only in failure messages (`MSG:5003`, `MSG:2019`).
- Index k ↔ `_t0k` suffix confirmed.
- `makemkvcon64.exe` is at `C:\Program Files (x86)\MakeMKV` and is not on PATH.
- MakeMKV does not create the destination folder.
- The info run and the rip must use the same `--minlength`.
- One optical pass: `--decrypt backup disc:N` gives a single extensionless image; mount it and
  run info and mkv off NVMe.

### OCR — measured, Tesseract 5.3.4, ~30 preprocessing combinations

Decorative captions unusable, plain UI text clean on every screen. Key on button labels, not
banners. Manual labelling stands; OCR is an assist, never the mechanism.

### Proven working in the Python

IFO parsing (menu PGCI_UT walk, language units, every PGC including cell-less ones, cell
playback tables, program maps, TT_SRPT) · PGC command tables at PGC `+0xE4` · full VM decode,
all seven instruction types, all five `if` prefixes · PGC chain resolver with 16 GPRMs + SPRMs,
`LinkPGCN`, `LinkTailPGC`, `LinkGoUp/Next/PrevPGC`, `JumpSS`/`CallSS`, cycle detection on
(domain, pgc, block, line) · ISO mounting · rip binding 6/6 on the real disc.

---

## Closed doors — do not reopen without new information

- `--decrypt backup` for both menu info and rips. One optical pass.
- `libdvdcss` rejected.
- TVDB for episode names: dead.
- Per-title output folders: dead. (Per-title *runs* are live again only as one side of the open
  log-naming question.)
- Exact-byte join, join on field 24: both wrong.
- DVD only. Blu-ray menus are BD-J.
- Tkinter or any standalone naming UI: dead.
- The `names.csv` round trip: dead.
- Non-modal backup popup: dead.
- A real tab control for ripping: dead.
- 4:3 aspect correction of the menu frame: dead.
- Name rows along the bottom / chips on the disc title row: dead.
- Chip ranking: dead.
- Two settable paths (ISO in, rips out): dead.
- The `menumap.json` loader / any outside file as app input: dead.
- Hard-coded sample data in the view: dead.
- Changing the theme, or a Rip-only style, to give buttons a disabled look: dead.
- Hidden Start button: dead — shown disabled with a tooltip, except that a missing Destination
  does not disable it (09-20).
- Drives box locked to the disc column's width: dead — own splitter.
- Remux/Transcode folder split on this branch: dead — Step 12 if ever.
- **Offering to open the folder picker from the no-Destination message: dead (09-20).**
- **`<Destination>\Discs\<DISC>\` as the record's home: dead — `Rip\<DISC>\Menu\` (09-20).**

---

## Reference

- Offsets and opcodes come from libdvdread `ifo_types.h` / `nav_types.h` and libdvdnav
  `vm/vmcmd.c`. **Fetch the source when extending — do not work from memory.**
- Drive `D:` = MakeMKV `disc:1`; drive 0 = `E:`; mounted ISO showed as `J:`.
- **Drive vs backup:** IFO parsing, command tables, resolver, coverage and join all work
  straight off the optical drive — CSS scrambles VOB payload, not IFOs or NAV packs. VMGM menu
  frames render off the drive; **VTS-domain frames do not**, and the episode index is
  VTS-domain. That is the only reason the backup exists.
- Test disc `THE_CENTENNIAL`: 6 titles, 2 with menu buttons, 6/6 bound. Its `menumap.json`: 6
  titles, 35 menu PGCs across 5 IFOs, 26 screens of which 6 carry images, 6 button sets, 17
  buttons. Source screens per title: 1 and 2 have one each, 3 and 5 have three each, 4 and 6
  have none.
- **Bugs fixed, do not reintroduce:** NAV pack detection must validate both PES headers, not
  just `00 00 01 BA` · `btn_ns` is at `hl_gi+0x11`, not `+0x13` · staging needs sector-aligned
  copy with zero-fill · don't abort on consecutive unreadable sectors, only if nothing reads in
  the first 512 · font lookup fell back silently to a tiny bitmap font in early builds.
- **XAML comments cannot contain `--`.** Cost a build this session.
- `Path` is ambiguous in `RipView.xaml.cs` (`System.IO` vs `System.Windows.Shapes`) — qualify it
  in full.
