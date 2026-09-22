# movie_namer - Session Handoff

**Date:** 2026-09-21 (supersedes 2026-09-20)
**Status:** backup, disk space, the IFO read from the mounted ISO, the rebuilt jobs list, and
port seams 2, 3 and 4 plus title coverage are in. On THE_CENTENNIAL the app matches the oracle's
`menumap.json` on titles, all 26 screens, 6 button sets / 17 buttons and all 17 resolved
targets. Picking a disc now shows its menu pictures with the buttons outlined. Nothing after the
IFO read is ported: no MakeMKV info, no rip, no binding, no naming.
**Home:** TranscodeTools (WPF, C#) - `C:\Users\andy\source\repos\Transcode-Tools`, solution
`Transcode Tools.sln` at the repo root, project in `TranscodeTools\`.
**Branch:** `feature/ripping`, cut from `wpf-rewrite`. Last pushed commit `35ec547` (seam 3).
The last round (title coverage, menu pictures, Title 1 auto-select, compare script update) was
handed over as whole files; committing it is Andy's.
**Reference only:** `tools\oracle\dvdmenumap.py` in the repo. The oracle run to compare against:
`C:\temp\Claude\movie_namer\work\THE_CENTENNIAL\menumap_out\menumap.json`.

---

## Pick up here

1. **Andy's questions about the screens and titles.** He raised them at the end of the session
   and deferred them to the next one. Start there; nothing about them is recorded yet.
2. **Run the updated compare script once.** The last run on Andy's machine used the previous
   script, so the pictures and title coverage were not checked through the app yet (they were
   checked here against `menumap.json`: same 6 pictured screens and names, coverage identical
   for all 6 titles). A pass prints
   `screens with a picture in the oracle: 6; titles with coverage checked: 6` and ends `MATCH`.
   ```
   python tools\oracle\compare_menumap.py C:\temp\Claude\movie_namer\work\THE_CENTENNIAL\menumap_out\menumap.json F:\rips\Rip\THE_CENTENNIAL\Menu\disc.json
   ```
3. **Next work - Andy's choice, not made:**
   - **(A)** info + rip + binding as one piece: `makemkvcon info` on the ISO and its parser
     (seam 5), `makemkvcon mkv` into `Rip\<DISC>\` (one run for all titles, `rip.log`), and the
     binder (seam 6, field-27 key + field-11 guard). Fills Runtime and File, and Bound/Verified.
     Small decisions left inside it: the rip's space reservation, and when the ISO is deleted
     (settled as the last step after rips are checked, but it sits with naming).
   - **(B)** name boxes first, saving typed names on the record; renaming waits for rips.
   Andy asked for bigger chunks per round ("why so slow, can't we do more at once?"), so (A)
   was offered as one piece.

---

## What was built this session

All under `TranscodeTools\Ripping\` unless noted.

### Backup (drive row)

- **`BackupJob.cs`** - two makemkvcon runs. `-r --cache=1 info disc:9999` lists the drives and
  the app finds the `DRV:` line whose 7th field is the drive letter, so the index is looked up
  every time (other machines' letters differ). Then
  `backup --decrypt -r --progress=-same disc:N <Destination>\ISO\<DISC>.iso`. Success is exit
  code 0 **and** the ISO existing. PRGV drives the progress. Log `Logs\Backup\<DISC>.log`:
  header with the drive, both commands, all output except PRGV, last line
  `Backup complete: ...` / `Failed: ...` / `Cancelled.` Partial ISO deleted on failure or cancel.
- **Drive row:** button Backup -> Cancel (running) -> Eject (success) / Log (failure). Row fills
  green with progress, red on failure (`RipColors`, translucent, theme stays frozen). Rows are
  updated in place so a refresh never resets a running backup. Eject is
  `IOCTL_STORAGE_EJECT_MEDIA`.
- **Prompts:** ISO present -> "X already exists. Overwrite?"; ISO gone but log present -> "X has
  been processed before. Back it up again?"
- **Redo rule:** a repeat backup (after Yes and the space check) removes the disc's job row,
  deletes `Rip\<DISC>\Menu\disc.json` and drops it from the disc list before MakeMKV starts.
  Only `disc.json` is deleted. Refused while that disc's IFO read is running.
- **`AppSettings.cs` / `UserPreferences.xaml(.cs)`** - `MakeMKV_Path`, a seventh Tool Paths
  row. Not in `AllPathsSet()`; Backup refuses with a message when it's empty.

### Disk space - `DiskSpace.cs`

- Checked **when Backup is clicked**: free space on the Destination (`GetDiskFreeSpaceEx`, works
  for network paths) must cover **1.25 x** the size Windows reports for the disc. An ISO about
  to be overwritten counts as free. Refusal: message box "Insufficient Disk Space Available".
- Held while running: `Reserve\<DISC>.reserve` (hidden folder), sized with `SetLength` (claims
  the space on NTFS without writing), delete-on-close, shortened every 2 s by the ISO's size.
  Running backups need no running totals: their space is really held, so Windows' free-space
  figure already excludes it. Clicks are handled one at a time on the UI thread.
- `Reserve\` swept when a Destination loads (files a running backup holds refuse deletion).
- Andy confirmed the space behaves as expected.

### IFO read from the ISO - `IsoMount.cs`

- PowerShell `Mount-DiskImage -NoDriveLetter -PassThru`, `Get-Volume` for the
  `\\?\Volume{...}\` path, `Dismount-DiskImage`. ISO path passed in the `TT_ISO` environment
  variable; progress output off; failure = exit code. Already mounted with a letter -> a hand
  mount, read in place and left mounted. Mounted without a letter -> the app's own leftover,
  read then unmounted.
- The job no longer touches the drive. Eject is safe at any time. Two jobs can't read one ISO.
  The record's `SourcePath` names the ISO.

### Jobs list

- Rebuilt when a Destination loads: one row per backup log ending "Backup complete" whose ISO
  exists; Drive from the log's `Drive:` line; status is the record's counts if it has one, else
  "ready". Every row keeps Start (re-run, no prompt). A new backup replaces a disc's row.
- The Destination is **not** reloaded automatically at launch; the rebuild runs when it is
  picked. Not discussed; would be its own change.

### Seam 2 - VM decode and button scan

- **`VmCommand.cs`** - port of the oracle's `decode_command` (libdvdnav `vm_print_mnemonic`):
  text plus the structured link/jump, `if`, `set` and system-set parts.
- **`Ifo.cs`** - each menu PGC's pre/post/cell command tables (`pgc + 0xE4`), in memory only.
- **`NavPack.cs`** - NAV pack detection (both PES headers) and button parsing, oracle's
  junk-rectangle rule.
- **`MenuScan.cs`** - `MenuVob` reads `VIDEO_TS.VOB` / `VTS_nn_0.VOB` off the mounted ISO,
  scans **every cell in full**, fills `NavPacks` and `ButtonSets`. Also `CopySectors` for seam 4.

### Seam 3 - resolver

- **`Resolver.cs`** - port of the oracle's `Resolver` (16 GPRMs, oracle SPRM defaults, step 400
  / depth 40 limits, loop detection). Fills each button's `Resolved`, with trace and notes.
  Buttons are re-decoded from their saved raw bytes.

### Title coverage and seam 4 - pictures

- **`TitleCoverage.cs`** - port of `title_coverage`: each title's `NamedBy` (buttons whose
  target is the title, with IFO, PGC, cell, button set, button) and `ReachedFrom` (PGC commands
  that JumpTT / JumpVTS_TT / JumpVTS_PTT to it, sorted).
- **`MenuFrames.cs`** - port of `keep_screen` + `grab_menu_image`: a screen is pictured unless
  every button provably leads to a menu or exit; the cell's first 3,000 sectors are cut into
  `Menu\cell.vob.tmp` and ffmpeg (Preferences path) takes a frame at 0.7 s, else 0 s. Saved as
  `Menu\<IFO>_<menu>_pgc<N>_cell<N>.png` (oracle names). Screens with the same sectors share a
  picture. The oracle's naming-sheet images are not made; the view draws outlines live.
- **`RipView.xaml.cs`** - opening a disc selects Title 1 (Andy: "otherwise it feels like its
  not working").

### `tools\oracle\compare_menumap.py` (repo root)

Checks `disc.json` against `menumap.json`: titles, screens and sectors, NAV packs, every button
field, resolved targets with trace and notes, picture names, title NamedBy / ReachedFrom. The
old Python run only read 8,000 sectors per cell; for a longer cell the oracle's sets must be the
first sets the app found, and it prints something only if the app found extra ones.

---

## Decided this session

- **Log names:** one log per stage in `Logs\<timestamp>\<DISC>\` - `info.log`, `rip.log`,
  `naming.log`.
- **MakeMKV path requirement** will tie to Rip mode once the mode checkboxes exist; until then
  it's outside `AllPathsSet()`.
- **Reservation: 1.25 x the disc's reported size for the ISO**, released when the backup ends.
  Supersedes disc size x 2. The rip's reservation is decided when that stage is ported.
- **Space check on click, message-box refusal.** No waiting, no queue, no timed re-check. Andy
  dislikes that Backup is clickable without the space but chose this.
- **IFO read stays the second process and reads the ISO**, not the disc. Measured: all five
  IFOs byte-identical on disc and in the ISO.
- **Mount through Windows** (PowerShell), no drive letter. Chosen over DiscUtils or an own ISO
  reader as "most efficient and maintainable".
- **Real-drive detection: do not change it** ("it works with no issues").
- **Stage tracking:** backup log marks stage 1; `disc.json` marks the IFO read for now. "Log
  management" is its own parked item.
- **Jobs list:** discs with a record stay in the list (Andy is still thinking about how to manage
  it in the end); every row keeps Start.
- **Re-read prompt:** not built. Andy is between asking whenever a record exists and gating on
  rip files existing. Decide when rip or naming lands.
- **Redo = redo everything downstream;** record deleted.
- **Seam 2:** split 2a / 2b, tested together; cells scanned in full (the 8,000-sector cap had no
  recorded reason - Andy: "there is no reason not to"); no VOB staging; only button text + raw
  on the record; proof is comparison with the oracle run, no test project.
- **Ripping's one-file-per-concern layout is fine;** refactoring the rest of the app is known
  and not a priority.
- **No keep-awake on the backup:** `PowerKeepAwake` doesn't work; Andy's PC is set never to sleep.
- **Pace:** Andy wants bigger chunks per round and fewer stops; stop only for real choices.

---

## Measured this session

- MakeMKV 1.18.3: passing `<...>\ISO\<DISC>.iso` with no trailing backslash writes the image
  under exactly that name (MakeMKV's message still says "into folder").
- `DRV:` lines carry the drive letter as a 7th field. On Andy's machine D: = `disc:1`,
  E: = `disc:0`. The info run's `MSG:5010 "Failed to open disc"` is disc:9999 and harmless.
- Disc size before any read: 8,191,078,400 - the ISO minus one 2,048-byte sector
  (ISO 8,191,080,448).
- The ISO is not preallocated: 0 bytes during the scan, then grows in 32 MiB steps.
- Mount ~4.08 s, IFO read from the ISO ~468 ms, unmount ~62 ms; IFO read off the drive ~375 ms
  (probably helped by cache).
- Bus types: Andy's real drives report 8 (RAID - SATA controller in RAID mode), a mounted ISO
  15. Not used; recorded in case.
- `-NoDriveLetter` mount reads fine through `\\?\Volume{...}\`.
- Full scan of `VTS_01_0` root (9,562 sectors): 59 NAV packs vs the old run's 50, no extra
  button sets.
- Seams 1-3 on Andy's machine: MATCH on everything compared.

---

## Known, not fixed

- Closing the app during a backup leaves makemkvcon running.
- An ISO mounted by hand shows in the drives box with a Backup button (the app's own mounts
  don't, having no letter).
- The Destination isn't reloaded at launch.
- A redo deletes only `disc.json`; old pictures stay until the next read overwrites them.
- GridView stub columns; cell text slightly left of headers (Step 12).
- .NET mismatch: csproj `net8.0-windows`, reference doc says .NET 9.

**Docs known stale:** `Development_Rules.md` (zip workflow), `TranscodeTools_Reference.md`
(Remux/Transcode only), repo `README.md` (other-transcode).

*Queue, parked:* log management · Preferences mode checkboxes (Rip/Remux/Transcode) with gated
tool paths and an info/rip job cap · the re-read prompt rule · the rip's reservation · auto-start
on disc insert · OCR assist · disc fingerprint · Blu-ray contact sheet · Help dialog · cheap
check never run: `TXTDT_MGI` at VMG `0xD4`.

---

## Standing

### Destination layout

```
<Destination>\
    ISO\<DISC>.iso
    Rip\<DISC>\                     remux points here
        *.mkv                       (rip stage, not built)
        Menu\disc.json              the record
        Menu\<IFO>_<menu>_pgc<N>_cell<N>.png
    Reserve\                        hidden
    Logs\Backup\<DISC>.log
    Logs\<timestamp>\<DISC>\        info.log, rip.log, naming.log (not built)
```

### Layout

```
User Mode:  (Rip) (Remux) (Transcode)
[Destination] [history dropdown..............................]
drives box [Backup/Cancel/Eject/Log] || jobs list [Start/Cancel]
--------------------------------------------------------------
left column: discs -> disc titles | chip strip / menu picture
--------------------------------------------------------------
status
```

Theme is frozen (`FlatButton` has no disabled look) - do not reopen.

### Record shape

```
DiscRecord      SchemaVersion (1), Name, SourcePath (the ISO), CreatedUtc,
                RipFolder (not saved: set from where the record is found)
                Titles[], Screens[]                        (MenuGraph: still not saved)
TitleRecord     Number, Vts, VtsTitle, Angles, Chapters, Duration, Seconds, Segments,
                CompositeOf[], FileName, Bound, Verified, SourceBytes, FileBytes,
                NamedBy[] { Ifo, Pgc, Cell, ButtonSet, Button }   (filled)
                ReachedFrom[]                                     (filled)
                Flags { PlayAll, NoMenuButton }, Note, Name
ScreenRecord    Ifo, Domain, Lang, Menu, Pgc, Cell, Image (filled), FirstSector,
                LastSector, NavPacks (filled), ButtonSets[] (filled)
ButtonRecord    Number, X0, Y0, X1, Y1, Up, Down, Left, Right, CommandText, Raw,
                Resolved { Kind, Title, Chapter, Vts, VtsTitle, Chapters, MenuPgc,
                           Button (new), Domain, Why, Trace[], Notes[] }
```

### Typed names / Preview

The app renames the file on disk; a named disc shows the filename, editable by right-click.
Preview is a Plex naming-convention check.

---

## Facts that must survive the port (unchanged)

- **Join key = `TINFO` field 27** (output filename); **guard = field 11**, file within 0.95-1.00
  of it (measured 0.978-0.981). Not the row index, not the chapter count (gate accepts
  `TT_SRPT` or `TT_SRPT - 1`). The oracle's `parse_makemkv_info` still keys on field 24; port
  the field-27 decision.
- Play-all from data: multi-range segment map (field 26) + runtime = sum of spanned titles
  (+-2 s, 2-4 titles). Flag, don't hide.
- Unmatched: the file on disk gets `_unmatched_` prepended. Idempotent.
- A successful `mkv` run names no files (`MSG:3028` per title, `MSG:5005`); filenames only in
  failure messages. Index k = `_t0k`. MakeMKV doesn't create the destination folder. Info and
  rip must use the same `--minlength`. `makemkvcon64.exe` is in
  `C:\Program Files (x86)\MakeMKV`, not on PATH.

## Closed doors - do not reopen without new information

Everything in the 09-20 list stands, plus:
- Waiting for space, a queue, or a timed space re-check (09-21).
- The IFO read as the backup's last step, or off the disc (09-21).
- DiscUtils or an own ISO reader (09-21).
- Changing real-drive detection, e.g. by bus type (09-21).
- Disc size x 2 reservation (09-21).
- The 8,000-sector scan cap and VOB staging in the app (09-21).
- Keep-awake for the backup (09-21).

## Reference

- Offsets and opcodes: libdvdread `ifo_types.h` / `nav_types.h`, libdvdnav `vm/vmcmd.c` -
  fetch the source, don't work from memory.
- THE_CENTENNIAL: 6 titles; 35 menu PGCs; 26 screens, 6 pictured; 6 button sets, 17 buttons;
  118 PGC commands. Titles 4 and 6 have no naming button (reached from VMGM PGC 21 and PGC 5).
- XAML comments cannot contain `--`. `Path` is ambiguous in `RipView.xaml.cs`; qualify it.
- Claude can build the project in its container (`dotnet build -p:EnableWindowsTargeting=true`)
  and run pure Ripping code against the oracle's output, but can't run WPF or touch Andy's ISO.
