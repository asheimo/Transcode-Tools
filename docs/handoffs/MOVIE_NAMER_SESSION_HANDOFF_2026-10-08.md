# movie_namer - Session Handoff

**Date:** 2026-10-08 (supersedes 2026-09-21)
**Status:** Start on a job row now runs **Analyse -> Info -> Rip** as one job, and binds every
title to its ripped file. On THE_CENTENNIAL the compare script reports **MATCH** on titles,
26 screens, 17 buttons, MakeMKV info for 6 titles, and binding for 6 titles against the 6 real
.mkv files. The overwrite prompt and `--noscan` were tested and work. The **naming stage is
written and builds, but has not been run** - nothing in it is tested.
**Home:** TranscodeTools - `C:\Users\andy\source\repos\Transcode-Tools`, branch `feature/ripping`.
Last commit `9f23a0b` (the 09-21 round). **Nothing from this session is committed** - Andy's call:
commit once tested.

---

## Pick up here

1. **Get the untested files onto Andy's machine.** The session's last files (naming popup,
   Preferences Rip tab, ISO deletion) were never handed over - they are attached with this
   handoff. List under "Not yet tested".
2. **Test the naming stage** (criteria below). Andy wanted to see the Delete layout before
   judging it.
3. **Then Andy's choice, not made:** OCR pre-fill for the name boxes. Options below.

---

## Tested and working this session

- **Info stage** - `Ripping\MakeMkvInfo.cs`. `makemkvcon -r --noscan --minlength=0 info iso:<ISO>`,
  log `Logs\<yyyyMMdd_HHmmss>\<DISC>\info.log`. TINFO rows joined to titles by **field 24**;
  fills Duration, Seconds, SourceBytes (11), Segments (26), new `RipName` (27). Also the
  oracle's **chapter gate** (field 8 = TT_SRPT or TT_SRPT - 1, sets Verified) and **play-all**
  (several segment ranges, runtime = sum of 2-4 other titles within 2 s; sets Flags.PlayAll,
  CompositeOf). A title listed twice fails the run.
- **Rip stage** - `Ripping\MakeMkvRip.cs`. `makemkvcon -r --noscan --minlength=0 --progress=-same
  mkv iso:<ISO> all <Rip\DISC>`, log `rip.log` in the same timestamp folder. Status shows
  `ripping nn%`. On failure or cancel every .mkv in the folder is deleted (same reason as a
  partial ISO).
- **Binder** - `Ripping\RipBinder.cs` (not `Binder`: clashes with `System.Reflection.Binder`).
  Bound = RipName file present, size 0.95-1.00 of field 11, Verified. Unclaimed .mkv files get
  `_unmatched_` prefixed on disk; idempotent. Results in `rip.log`.
- **Start, before anything runs:** rips already in `Rip\<DISC>\` -> "X has already been ripped.
  Overwrite?" (Yes deletes them after the space check; they count as free). Space: 1.25 x the
  ISO's size free, else "Insufficient Disk Space Available"; held by a reserve file that shrinks
  as the .mkv files grow (`ShrinkWhileRunningAsync` now takes a `Func<long>`, shared with Backup).
- **Shared runner** - `Ripping\MakeMkv.cs`: the makemkvcon runner moved out of `BackupJob.cs`,
  used by Backup, Info and Rip. `BackupException` -> `MakeMkvException`. Backup unchanged.
- **Record** saved after each stage. Job Step shows Analyse / Info / Rip; rebuilt rows take
  their Step from the record. Status ends `6 titles, 26 screens, N bound`.
- **MakeMKV path check removed from Start** (a job row exists only after a backup that needed it).
- **`tools\oracle\compare_menumap.py`** takes an optional `titles.txt` and checks the info
  fields (via the oracle's `parse_makemkv_info`) and binding (via the oracle's `join_titles`
  over the real rip folder). Pass prints `titles with binding checked: 6 (6 .mkv file(s) ...)`.

## Not yet tested (written, builds clean)

- **Naming popup** - `Ripping\NamingWindow.xaml(.cs)` + `Ripping\TitleNaming.cs`. Opened by
  right-click on a disc title row -> **Name Titles...** (non-modal, one per disc, opens on the
  clicked title). One row per title + one per `_unmatched_` file: #, Runtime, File, Name box
  (bound titles only), Delete box, Note. Play-all titles start with Delete ticked.
  **Preview** problems (block Apply): illegal characters, trailing space or dot, duplicate
  name, name already used by another file, named and marked Delete. Warning (doesn't block):
  no `(YYYY)` and no `S01E01`. **Apply** asks "Rename N files and delete N?", renames to
  `<name>.mkv` (case-only renames via a temp name), deletes ticked files, writes
  `Logs\<timestamp>\<DISC>\naming.log`, saves the record, closes, and reports on the main
  status line. Stops at the first file it can't change.
- **Play in MPV** - same right-click menu; plays the title's file through `MainWindow.TryLaunch`.
- **ISO deletion preference** - Preferences, new **Rip** tab: "Delete the backup ISO: After Rip /
  After names are applied" (default After names are applied). After Rip deletes it at the end
  of the Rip stage (logged in `rip.log`; a failed delete fails the job but keeps the rips);
  the other deletes it at the end of Apply (logged in `naming.log`).
- `MainWindow.YearPattern`, `EpisodePattern` and `TryLaunch` changed `private` -> `internal` so
  the Rip code reuses them.

### Test criteria for the naming stage

1. Builds in Visual Studio.
2. Right-click a title -> Name Titles... opens the popup on that title; right-click again
   brings the same window forward. The main window's chips and picture still work while it's open.
3. Play in MPV plays a bound title; on a title with no file the status line says so.
4. Preview flags a duplicate name and an illegal character as problems and a name without
   (year)/S01E01 as a warning; Apply refuses while a problem remains.
5. Apply renames the files, the File column shows the new names, `naming.log` lists each one.
6. Preferences -> Rip tab: the setting saves, and Cancel restores it.
7. With "After names are applied": the ISO is gone after Apply and the job row leaves the list.

---

## Decided this session

- Info and Rip are one job with Analyse (Start runs all three). Two makemkvcon commands only
  because a successful `mkv` run names no files.
- Rip reservation: **1.25 x the ISO's size** (same factor as Backup).
- Re-run rule matches Backup: **ask "Overwrite?"**, not refuse.
- `--noscan` on info and rip (MakeMKV's docs: "Don't access any media during disc scan").
  Andy had seen drive activity; fixed and confirmed.
- `RipName` is its own field; `FileName` means only the file found on disk.
- Logs: one per stage under `Logs\<timestamp>\<DISC>\` (info.log, rip.log, naming.log) - already
  settled 09-21, kept.
- The two processes are named **Backup** and **Rip**.
- Naming: right-click a title row opens it; **popup, non-modal**; **all names applied at once**
  with Apply; per-row Delete; play-all starts ticked; Preview kept.
- **ISO deletion is a preference**: after Rip, or after names are applied. Lives on a Rip tab.
- Disc folder rename (`Rip\<DISC>\` -> a movie name for Remux): **parked**, Andy is thinking it over.

## Measured this session

- `TXTDT_MGI` pointer (VMG `0xD4`) on THE_CENTENNIAL is **0** - no disc text data.
  `tools\oracle\txtdt_probe.py`.
- Plain-text scan of every IFO and menu VOB (`tools\oracle\text_probe.py`, output read in full):
  the only real text is the `DVDVIDEO-VMG` / `DVDVIDEO-VTS` headers; the 9,000-odd VOB hits are
  noise from compressed video (no real words). **The names are pictures, not text** - in the menu
  video or the subpicture overlay. The probe's word filter is too loose; tighten it before reuse.
- Andy's ffmpeg has **no `ocr` filter** (list read in full).
- The VOBs must be read from the mounted ISO, not the disc (CSS).

## OCR options (Andy's choice, not made)

1. **Windows' built-in OCR (`Windows.Media.Ocr`)** - nothing to install; the csproj target moves
   from `net8.0-windows` to a Windows 10 version (e.g. `net8.0-windows10.0.19041.0`). Crop each
   button's rectangle from the saved menu picture and read it. Recommended.
2. Tesseract as its own program with a Preferences path row.
3. An ffmpeg build with libtesseract (plus language data).
Either way it pre-fills the name box only; August found decorative caption fonts read badly,
and this disc's labels are that kind.

---

## Known, not fixed

- A naming window left open while the same disc's Start re-runs holds the old record; Apply
  would then fail on missing files (visibly, via the IOException path).
- Closing the app during a backup or rip leaves makemkvcon running.
- An ISO mounted by hand shows in the drives box with a Backup button.
- The Destination isn't reloaded at launch.
- A redo deletes only `disc.json`; old pictures stay until the next read overwrites them.
- .NET mismatch: csproj `net8.0-windows`, reference doc says .NET 9.
- Docs stale: `Development_Rules.md` (zip workflow), `TranscodeTools_Reference.md` (no Rip mode),
  `README.md`.

*Queue, parked:* OCR pre-fill · disc folder rename · log management · Preferences mode
checkboxes with gated tool paths and a job cap · auto-start on disc insert · disc fingerprint ·
Blu-ray contact sheet · Help dialog.

---

## Files changed since `9f23a0b`

New: `Ripping\MakeMkv.cs`, `MakeMkvInfo.cs`, `MakeMkvRip.cs`, `RipBinder.cs`, `TitleNaming.cs`,
`NamingWindow.xaml`, `NamingWindow.xaml.cs`; `tools\oracle\txtdt_probe.py`, `text_probe.py`.
Changed: `AppSettings.cs`, `MainWindow.xaml.cs`, `UserPreferences.xaml(.cs)`,
`Ripping\BackupJob.cs`, `DiscRecord.cs`, `DiscStore.cs`, `DiskSpace.cs`, `RipJob.cs`,
`RipView.xaml`, `RipView.xaml.cs`; `tools\oracle\compare_menumap.py`.

## Standing (unchanged unless noted)

Destination layout, ripping layout, record shape, the port facts (field-27 key / field-11 guard,
play-all, `_unmatched_`, MakeMKV path) and the closed-doors list are as in the 09-21 handoff.
Record additions: `TitleRecord.RipName`. Settings addition: `AppSettings.IsoDeleteAfter`
(`AfterRip` / `AfterNaming`).
