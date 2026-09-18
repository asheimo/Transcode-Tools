# movie_namer — Session Handoff

**Date:** 2026-09-18 (supersedes 2026-09-16)
**Status:** first C# is in. Rip mode, the Rip page layout, the disc record models and their
save/load are committed and pushed. No engine module is ported yet; Start reports that.
**Home:** TranscodeTools (WPF, C#) — `C:\Users\andy\source\repos\Transcode-Tools`, solution
`Transcode Tools.sln` at the repo root, project in `TranscodeTools\`.
**Branch:** `feature/ripping`, cut from `wpf-rewrite`. Head `2da3259`.
**Reference only:** `tools\oracle\dvdmenumap.py` (~2240 lines) in the repo.

---

## Where the code stands

### Commits

On `wpf-rewrite`:

- `f7873f6` Add project docs to the repo
- `cc50eb8` Move docs to the repo root — `docs\` out of the project folder, `docs` solution
  folder with nested `handoffs`, the csproj `Folder` entry removed

On `feature/ripping` (above those):

- `d6bb40b` Preserve dvdmenumap.py as the port oracle
- `2cee2a8` Add Rip mode and first-pass RipView layout
- `a2979dc` Merge `wpf-rewrite` into `feature/ripping`
- `3749942` Move the oracle to the repo root — `tools\oracle\` out of the project folder, stray
  `commit_msg.txt` removed
- `2da3259` Add Start buttons and a separate splitter for the drives box — **this commit also
  carries the disc record models and the view binding** (`DiscRecord.cs`, `DiscStore.cs`); its
  message only describes the Start work

MEASURED 2026-09-18: every file on `feature/ripping` matches what was handed over. The branch
builds with 0 errors and 2 warnings; both warnings are the csproj `FileVersion` `1,1` format,
present before this work.

### Repo layout

```
Transcode-Tools\
  Transcode Tools.sln        docs solution folder -> docs\, handoffs\
  docs\                      TranscodeTools_Reference.md, Development_Rules.md
    handoffs\                this file, 2026-09-16, and the older 2026-09-15 one
  tools\oracle\              dvdmenumap.py, examples\ (2 PNGs, names_example.csv)
  TranscodeTools\
    UserMode.cs              Rip / Remux / Transcode
    Ripping\                 DiscRecord.cs, DiscStore.cs, RipView.xaml(.cs)
```

`tools\oracle\examples\` does **not** hold THE_CENTENNIAL's `menumap.json` or its frames.

### What runs today

- **User Mode:** Rip (leftmost), Remux, Transcode. The app opens on the leftmost visible button.
  - One `UserMode` value drives the mode; `_isTranscodeMode` is a read-only property over it, so
    the 25 existing uses are unchanged.
  - `IsChecked` is set in the constructor (`SelectStartupMode`), not in XAML. A Checked event
    raised from XAML fires before the controls `ModeChanged` touches exist.
  - The existing full reset on mode switch still runs.
- **Rip mode hides:**
  - the Remux/Transcode content;
  - the bottom button row;
  - the File menu's Input/Output items, Run and Run's separator.
- **Rip mode shows** "Open Destination Directory" and History → "Clear Destination History".
- **Destination:**
  - button + history dropdown;
  - history persisted in `AppSettings.RecentDestinationFolders`, trimmed by the shared history
    size;
  - the box starts empty at launch, like Input/Output.
- **Drives box:**
  - real optical drives with volume label or "No disc";
  - refreshes on `WM_DEVICECHANGE` (disc inserted/ejected);
  - the scan runs off the UI thread.
- **Start:**
  - one per drive row, always visible;
  - enabled when the drive has a disc and a Destination is chosen;
  - tooltip gives the reason when disabled.
  - Click currently puts "The disc engine is not built yet, so nothing runs" on the status line.
- **Disc list:**
  - read from `<Destination>\Discs\*\disc.json` when a Destination is chosen;
  - unreadable records are named on the status line with the reason.
- **Disc → disc titles:**
  - selecting a disc switches the left column to its titles;
  - a "Back to discs" button returns.
- **Chips:**
  - selecting a title fills the chip strip from `NamedBy`, one chip per screen + button set;
  - the first screen is shown straight away.
- **Picture:**
  - sized to the frame's own pixels inside a shrink-only Viewbox;
  - buttons outlined and numbered;
  - naming buttons drawn thicker in the accent colour.
- **Right-click on a disc row** opens its rip folder, or says it wasn't found.
- **Jobs list:** columns only, not wired.

---

## Decided 2026-09-18

### Build order — supersedes "model classes + menumap.json loader next"

- **UI first, then wire classes and methods to it.**
- **No external files into the app.** Everything becomes C# with collections. The only files
  the app reads back are its own per-disc records, for reloading. The `menumap.json` loader is
  dead.
- **Models first, filled by the engine** — no hard-coded sample data. The view stays empty
  until a port module writes a record.

### Mode switch and menus

- Three-way mode as above. Andy trusted best practice for the three-way radio: one enum as the
  single source of truth, one shared `Checked` handler.
- In Rip mode the bottom button row is removed — none of its buttons apply.
- Rip mode's File menu gets menu items for the directory it sets, named to match
  ("Open Destination Directory", "Clear Destination History"). Run is hidden: there is no Run
  window for Rip.

### Disc record storage

- `<Destination>\Discs\<DISC>\disc.json` plus that disc's cached frames. Same pattern as
  Remux/Transcode settings (`<Input>\Remux\<movie folder>\<file>.txt`): a mode folder with a
  subfolder per item, everything under Destination.
- One JSON file per disc via System.Text.Json, as `AppSettings` does. The Remux/Transcode
  `.txt` load code is not reused — it regex-parses a command line.
- Save writes `disc.json.tmp` and then moves it over `disc.json`, so a crash can't leave a
  half-written record.
- **The disc list belongs to the Destination.** A different Destination shows that folder's
  discs.
- **Menu graph left off the model** until the resolver port defines its shape. Records saved
  before then just lack the field.

### Record shape — revised after reading the oracle

- `ScreenRecord` holds `ButtonSets[]`, each with `Buttons[]`. MEASURED: the script keeps every
  distinct button set found across a cell's NAV packs, so one screen can carry several. A
  screen with two sets shows two chips on the same picture.
- `ButtonsImage` dropped. It was the naming-sheet PNG; the view draws hotspots live over the
  plain frame.
- `NamedBy` = IFO, PGC, cell, button set, button — not an image path.
- Button rectangles stored as corners `X0, Y0, X1, Y1`, as the script does.
- `ResolvedTarget` also keeps `Chapter`, `Domain` and `Why`; the resolver produces them.
- Field names follow the script so the oracle comparison lines up.
- **PAL:** MEASURED — the script validates rects against 720 × 576. The picture container is
  sized from the frame's own pixels, not a fixed 720 × 480.

The record as written:

```
DiscRecord      SchemaVersion (1), Name, SourcePath, CreatedUtc, RipFolder
                Titles[], Screens[]                        (MenuGraph: later)

TitleRecord     Number, Vts, VtsTitle, Angles, Chapters
                Duration, Seconds, Segments, CompositeOf[]
                FileName, Bound, Verified, SourceBytes, FileBytes
                NamedBy[] -> { Ifo, Pgc, Cell, ButtonSet, Button }
                ReachedFrom[]
                Flags -> PlayAll, NoMenuButton
                Note, Name

ScreenRecord    Ifo, Domain, Lang, Menu, Pgc, Cell
                Image, FirstSector, LastSector, NavPacks
                ButtonSets[] -> Number, Buttons[]

ButtonRecord    Number, X0, Y0, X1, Y1, Up, Down, Left, Right
                CommandText, Raw
                Resolved { Kind, Title, Chapter, Vts, VtsTitle, Chapters,
                           MenuPgc, Domain, Why, Trace[], Notes[] }
```

### Starting a disc

- **A Start button on each drive row.** An auto-start checkbox can be added later on top of the
  same job code.
- **The drives box has its own splitter**, independent of the disc column. The drives/jobs band
  has its own columns (drives 350 px, min 250); the lower splitter moves only the disc column.
- **Start is shown disabled** when it can't be used, not hidden. The tooltip says why ("No disc
  in this drive." / "Choose a Destination first."). Before a Destination is chosen the status
  line adds "Choose a Destination to start a disc."
- Cancel belongs on the job row, not the drive row — a job outlives its drive.
- Until the rest of the engine lands, Start runs only what has been ported, and each port
  module joins the job as it lands.

### Theme is frozen

- MEASURED: `FlatButton` in `ThemeResources.xaml` has hover and pressed triggers but no disabled
  state. A disabled button looks clickable — Start here, and already View Command (Remux) and
  Start / Cancel-Close in the Run window.
- **Andy's decision: no change.** Not to the theme, and not a Rip-only style either. Do not
  reopen.

### Folder layout

- Andy asked whether Remux/Transcode get folders to match `Ripping\`. No, not on this branch.
  MEASURED: `MainWindow.xaml.cs`, `RunRemux.xaml.cs`, `FfprobeService.cs`, `Models.cs` and
  `CommandBuilder.cs` each handle both modes, so a split is a refactor, not a move. Any split
  belongs to Step 12. Moving files later is cheap — the namespace is plain `TranscodeTools`
  everywhere.
- `dvdmenumap.py` and `examples\` are kept in the repo as historical items. The oracle commit
  sits on `feature/ripping`, not `wpf-rewrite`; it reaches the main line when the branch merges.

### Implemented without a separate decision — revisit if wanted

- Chip label `VTS_01_0 pgc 9`, plus ` set N` when a screen has more than one set. Full location
  in the tooltip.
- Selecting a disc title shows its first screen immediately.
- "Back to discs" button above the title list, with the disc name beside it.

---

## Open — pick up here

**Next task: the IFO parser (port seam 1: IFO structures + sector reader + staging),
driven by Start.**

- Pressing Start on D: reads THE_CENTENNIAL's title table (TT_SRPT) and menu structure straight
  off the drive. CSS scrambles VOB payload, not IFOs, so no backup is needed for this part.
- The job appears in the jobs list.
- The disc record is written to `Discs\THE_CENTENNIAL\disc.json`.
- The disc titles appear with real numbers, VTS, angles and chapter counts.
- Filenames and runtimes arrive with the MakeMKV info/rip port; pictures arrive with frame
  extraction.

To settle with Andy **before writing** it:

- The job row's shape — what Disc / Step / Status show, and where Cancel goes on the row.
- What the record's `RipFolder` is set to before any rip exists.
- Whether a second Start on a disc that already has a record overwrites it, asks, or refuses.

**Open design detail, not blocking:** what an expanded disc title row shows, including where the
name is typed. Double-click expand and name entry were left out of the first pass on purpose.
Settle once the view has real data.

**To measure on Andy's hardware:**

- What size Windows reports for a disc's drive letter before anything is read. The backup
  reservation (disc size × 2) depends on it.
- Whether MakeMKV's backup file grows on disk as it writes. Shrinking the reserve file depends
  on watching that growth; if the file only appears at the end, use MakeMKV's progress output.

**Known, not fixed:**

- **A mounted ISO shows up in the drives box**, because Windows reports it as an optical drive,
  and it gets a Start button. Needs a real-drive check before the app starts mounting its own
  ISOs.
- **GridView stub columns:** the drives, jobs and disc lists show an empty header segment on the
  right. Cosmetic.
- **Cell text alignment:** cell text sits slightly left of its header. Same in the existing
  Remux/Transcode lists — the theme's row template ignores cell padding. Step 12.
- **.NET version mismatch:** MEASURED — the csproj targets `net8.0-windows`, but the reference
  doc and earlier handoffs say .NET 9. One of them is wrong.

**Docs known stale:**

- `Development_Rules.md` still describes the zip workflow and the v1/v2 zip naming. Both are
  dead: files come from GitHub, and changes go back as whole files or a git bundle.
- `TranscodeTools_Reference.md` still describes the app as Remux and Transcode only — no Rip
  mode, no pipeline, no preferences change.
- The repo `README.md` still says the transcode side wraps other-transcode, which was removed at
  Step 2.

*Queue, parked:*

- Auto-start checkbox (Start on disc insert).
- Preferences panel + gated app locations + the info/rip job cap.
- OCR pass: button-label OCR → keyword classification → screen ranking.
- Disc identity by fingerprint.
- Blu-ray contact-sheet generator.
- Help dialog.
- **Cheap check, never run:** `TXTDT_MGI` at VMG offset `0xD4` may hold real title text. Almost
  always empty on commercial discs; would beat OCR outright.

---

## Standing from 2026-09-16

### Rip is a mode

- Third User Mode radio button, "Rip", leftmost, same design as the others.
- Once the Rip / Remux / Transcode preferences exist, an unchosen mode loses its button and the
  app opens on the leftmost one left.
- Rip mode's content is its own control, `Ripping\RipView.xaml`. `MainWindow.xaml` (~52 KB) and
  `MainWindow.xaml.cs` (~137 KB) are not where the new layout goes.

### Layout

```
User Mode:  (Rip) (Remux) (Transcode)
[Destination] [history dropdown..............................]   <- full width
drives box  [Start]  ||  jobs in flight                           <- own splitter
--------------------------------------------------------------
left column (270, resizable) |  chip strip (fixed height)
  discs -> disc titles       |  menu frame
--------------------------------------------------------------
status
```

- **Vocabulary:** a *disc title* is a title row on the disc. It is kept separate from the
  *name* typed for it.
- A disc title row is one line: number, runtime, filename. Chips are not on the row.
- **Chip strip above the picture:** one chip per source screen, in the order found — no ranking.
  It always keeps its height, so the picture never moves between rows. Clicking a chip loads
  that screen and highlights the button.
- **Picture at its original stored size**, square pixels, no 4:3 correction. When space is short
  it shrinks with the aspect ratio kept; it is never enlarged. Picture and hotspot layer share
  one container.
- MEASURED 2026-09-18 on Andy's screen at the default window: the picture area is about
  683 × 490, so an NTSC frame shows at about 95%.
- Drives box and jobs list side by side — a job outlives its drive. The right side is empty in
  disc view. Open folder in Explorer is a right-click on the disc row. Copy the Run Remux
  pattern rather than refactoring toward it.
- Kept in case 4:3 correction is ever reopened: each menu domain records its own display shape
  — `vmgm_video_attr` in the VMG IFO, `vtsm_video_attr` in each VTS IFO, 2-bit
  `display_aspect_ratio` (0 = 4:3, 3 = 16:9), plus NTSC/PAL and picture size (libdvdread
  `ifo_types.h`). Not used.

### Destination

- **One settable folder** with a history list. Inside it the app keeps:
  - `ISO\<DISC>.iso`
  - `Rip\<DISC>\` — that disc's .mkv files
  - `Discs\<DISC>\` — the disc record and cached frames (added 2026-09-18)
  - `Reserve\` — hidden, see below
- Everything the app writes lives under Destination.
- The app creates `Rip\<DISC>\` before each rip — MakeMKV won't.
- `<DISC>` comes from the volume label. Same-label collisions are not pressing: box sets,
  usually TV, are labelled by season. Disc fingerprint stays parked.

### Jobs and disk space

- **Backup:** one job per drive.
- **Info and rip: no default limit.** Each ISO starts info and rip as soon as its backup
  finishes. A preference caps the count; it belongs with the preferences panel.
- **Reservation:** each backup reserves disc size × 2 from the start — its ISO plus its rips.
  That is the enforcement. No resizing after the info run.
- **Accounting:** a job starts only if *free space now − what every running job still needs ≥
  what this job needs*, where *still needs* = reservation − bytes written so far. Checks run one
  at a time.
- **Space held on disk:** each running job has a reserve file in `Reserve\`, opened with
  `FileStreamOptions.PreallocationSize` and delete-on-close. It shrinks as the job's real output
  grows. Closed at job end — success, failure or cancel — and Windows deletes it; also on crash
  or kill. A power cut or bluescreen leaves it behind, so `Reserve\` is swept at startup.
- Windows' own "Reserved storage" is the OS's, system-wide and admin-controlled. There is no
  per-app reservation without a file.
- **Short on space:** the job waits and its row shows the numbers ("waiting for space: needs X GB,
  Y GB free"). Refused only if the disc can't fit with nothing else running.
- **Over the reservation:** a disc that needs more than 2× (a play-all title duplicates its
  footage) carries on into ordinary free space; if that runs out, it fails visibly.
- **Every failure is obvious.** The job's row turns red and shows the program's own message. A
  failed backup leaves the disc flagged; a failed rip keeps its ISO (delete gate).

### Code layout

- `TranscodeTools\Ripping\` holds the whole feature: models, port modules, `RipView`.
- Namespace is plain `TranscodeTools`, not the folder default.

### Repo and branch

- Repo is public at `github.com/asheimo/Transcode-Tools`. Files are pulled from GitHub
  directly; there are no push credentials on Claude's side, so changes come back as whole files
  or a git bundle.
- Default branch is `wpf-rewrite`, not `main`.
- One long-lived feature branch holds the whole ripping/naming build — no unintended
  consequences for anyone trying the app mid-build.

### Docs

- `docs\` and `docs\handoffs\` at the repo root, outside the project folder so SDK globbing
  can't sweep them in; surfaced through a solution folder. The md is canonical; git carries
  history.

### Preview button

Kept, repurposed as a **Plex naming-convention check**, not a dry run. It calls the same code the
transcode side already uses — title case with the acronym list, resolution suffix after the
edition tag, missing-year detection.

### Typed names

The app renames the file on disk. Reopening a named disc shows the resulting filename in the name
field, not editable unless you right-click.

---

## The pipeline

```
backup → info → rip → naming
```

All four are new work. Unattended: backup, info, rip. Attended: naming only.

Analysis rides at the tail of **rip**, still unattended:
`IFO parse → VM decode → resolver → frame extraction → binding`. Because rip precedes naming,
discs arrive in the naming view already bound — no unbound row state, no lazy rebind. During
development the IFO parse runs first on its own, off the drive, because it is the first module
ported.

The ISO is deleted as the **last action**, after the ripped files have been tested. **Delete
gate:** every title bound (field 27 key, size inside the field-11 band) and frames cached. If
anything refuses, the ISO stays and the disc is flagged on its row. Nothing is lost — the discs
are still on the shelf.

**Vocabulary:** *Rip* = `makemkvcon mkv` extracting the .mkv titles out of the mounted ISO.
*Remux* and *Transcode* are the existing stages and are unchanged by any of this.

---

## Preferences panel

Three checkboxes — Rip / Remux / Transcode, all default checked. The app-locations panel requires
only the apps the chosen modes need; uncheck Rip and MakeMKV stops being a requirement. An
unchecked mode loses its User Mode button, and the app opens on the leftmost one left. The
info/rip job cap lives here too; a "Rip" tab would match the existing Remux and Encode tabs.

---

## Paths and guards

| Path | Settable | Notes |
|---|---|---|
| **Destination** | yes | one line, full width, history list. Holds `ISO\`, `Rip\`, `Discs\`, hidden `Reserve\` |
| **disc data** | **no** | `<Destination>\Discs\<DISC>\` — `disc.json` + cached frames |

- ISO and rips are always separate folders under Destination, so the delete step can never
  operate in the folder holding the output.
- **Not on C:** is Andy's own preference, not policy. Warn, never block.
- **Free space:** see *Jobs and disk space*.

---

## Persistence

The disc list persists across restarts through the disc records. A record must be
self-sufficient once the ISO is gone: titles, runtimes, bindings, hotspot geometry, cached
frames. It does not travel with the library; the filename does.

**Screen pruning must keep failing toward extra images, never a missing one.** With the ISO
deleted, cached frames are the only copy of the picture.

---

## The port

### Module seams

1. IFO structures + sector reader + staging — **next**
2. VM decode (libdvdnav `vm_print_mnemonic` port)
3. PGC chain resolver — defines the menu graph's shape on the record
4. Frame extraction
5. MakeMKV `titles.txt` parser
6. Rip binder
7. Rename engine
8. Views — first pass in

1–3 are pure functions over bytes with no I/O. If the port loses that boundary it has moved the
tangle rather than removed it. All modules live under `Ripping\`.

### Reuse

- **ffmpeg** — TranscodeTools already owns a process wrapper.
- **Settings load/save** — Destination history is in `AppSettings`. Disc records use their own
  JSON store (above).
- **Directory history** — Destination reuses the history pattern and history-size preference.
- ISO mount is PowerShell `Shell.Application` in the script (elevation-free, finds the letter by
  diffing the drive set). Port as a native `VirtualDisk` call or keep the shell-out; ~40 lines.

### Reporting

Every module needs a status/progress channel in its signature (`IProgress<T>` or an observable
collection). The Python prints to stdout; ported literally that becomes output that vanishes.

### Acceptance

`--selftest` covers the decoder, resolver and rip matcher and does not survive the port for
free. The Python stays under `tools\oracle\`: run both against the same disc and compare. Field
names on `disc.json` follow the script's, but the layout differs (button sets under screens,
corner fields named `X0..Y1`, NamedBy without image paths), so the comparison maps fields, not
bytes. Python retires when the comparison is clean, not when the code compiles.
`THE_CENTENNIAL` has a known-good run on Andy's machine (old `movie_namer\work\` folder).

---

## Facts that must survive the port

### The join

- **Key = `TINFO` field 27** (output filename). It carries MakeMKV's title index.
- **Guard = `TINFO` field 11.** The file must land between **0.95 and 1.00** of it. Measured
  0.978–0.981 across 9.5 MB to 7.8 GB. Field 11 is the source title size on the disc, never the
  size of the file written.
- The guard catches a `--minlength` desync between the info run and the rip.
- **Not** the row index. **Not** the chapter count — MakeMKV strips the trailing menu-jump cell.
  Gate accepts `chapters == TT_SRPT` or `TT_SRPT - 1`.
- The oracle's `parse_makemkv_info` still keys on field 24 and its docstring says so; the handoff
  decision (field 27 key, field 11 guard) is the one to port.
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

### OCR — measured, Tesseract 5.3.4, ~30 preprocessing combinations

- Decorative captions: unusable. Plain UI text: clean on every screen.
- Key on button labels, not banners.
- Manual labelling stands. OCR is an assist, never the mechanism.

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
- Per-title output folders: rejected.
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
- **The `menumap.json` loader / any outside file as app input: dead (2026-09-18).**
- **Hard-coded sample data in the view: dead (2026-09-18).**
- **Changing the theme, or a Rip-only style, to give buttons a disabled look: dead
  (2026-09-18).**
- **Hidden Start button: dead — shown disabled with a tooltip (2026-09-18).**
- **Drives box locked to the disc column's width: dead — own splitter (2026-09-18).**
- **Remux/Transcode folder split on this branch: dead — Step 12 if ever (2026-09-18).**

---

## Reference

- Offsets and opcodes come from libdvdread `ifo_types.h` / `nav_types.h` and libdvdnav
  `vm/vmcmd.c`. **Fetch the source when extending — do not work from memory.**
- Drive `D:` = MakeMKV `disc:1`; drive 0 = `E:`; mounted ISO showed as `J:`.
- **Drive vs backup:** IFO parsing, command tables, resolver, coverage and join all work straight
  off the optical drive — CSS scrambles VOB payload, not IFOs or NAV packs. VMGM menu frames
  render off the drive; **VTS-domain frames do not**, and the episode index is VTS-domain. That
  is the only reason the backup exists.
- Test disc `THE_CENTENNIAL`: 6 titles, 2 with menu buttons, 6/6 bound. Its `menumap.json`: 6
  titles, 35 menu PGCs across 5 IFOs, 26 screens of which 6 carry images, 6 button sets, 17
  buttons. Source screens per title: 1 and 2 have one each, 3 and 5 have three each, 4 and 6
  have none.
- **Bugs fixed, do not reintroduce:** NAV pack detection must validate both PES headers, not just
  `00 00 01 BA` · `btn_ns` is at `hl_gi+0x11`, not `+0x13` · staging needs sector-aligned copy
  with zero-fill · don't abort on consecutive unreadable sectors, only if nothing reads in the
  first 512 · font lookup fell back silently to a tiny bitmap font in early builds.
