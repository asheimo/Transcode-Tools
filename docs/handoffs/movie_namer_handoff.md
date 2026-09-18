# movie_namer — Session Handoff

**Date:** 2026-09-15 (supersedes 2026-08-14 evening)
**Status:** design continuing. Still no C# written.
**Home:** TranscodeTools (WPF, C#, .NET 9) — `C:\Users\andy\source\repos\Transcode-Tools`, solution `Transcode Tools.sln` at the repo root, project in `TranscodeTools\`.
**Reference only:** `dvdmenumap.py` (~2100 lines), previously `C:\temp\Claude\movie_namer\`.

---

## Decided this session

### Repo and branch

- Repo is **public** at `github.com/asheimo/Transcode-Tools`. Files can be pulled from
  GitHub directly — the per-session "upload a fresh solution zip, hand back a zip of
  changed files" workflow is retired.
- Default branch is **`wpf-rewrite`**, not `main`. 78 commits.
- **One long-lived feature branch cut from `wpf-rewrite`** holds the whole
  ripping/naming build. Not a series of short branches merged as each seam lands.
  Reason: no unintended consequences for anyone trying the app mid-build.

### Docs

- Both Word docs converted to markdown and retired. `TranscodeTools_Reference.md`
  and `Development_Rules.md` live in **`Transcode-Tools\docs\`** at the repo root —
  outside the project folder so SDK-style globbing can't sweep them into the build —
  surfaced in VS via a solution folder.
- The md is canonical. Version suffixes dropped; git carries history.
- The old "audit Word formatting drift" chore goes away with the docx.
- Handoffs live in **`docs\handoffs\`**, matching the convention used on other
  projects. `dvdmenumap.py` and `examples\` go under `tools\oracle\`.

### Known stale, not yet fixed

- `Development_Rules.md` still describes the zip workflow, including the v1/v2
  filename versioning rule. Both are dead.
- Neither doc knows about the ripping work. `TranscodeTools_Reference.md` still
  describes the app as remux and transcode only — the four-stage chain, the naming
  view, the port and the Rip/Remux/Transcode preference panel appear nowhere.
- The repo `README.md` still tells users the transcode side wraps other-transcode,
  which the reference doc records as removed at Step 2.

### Build order

**View first.** The rough naming view comes before porting the decoder, resolver and
binder — there has to be a view to test the engine from.

### Ripping tab layout — supersedes the 2026-08-14 wireframe

Everything lives in the tab. **No separate backup popup.** The popup existed only
because the Run windows are modal; a tab in the main window is non-modal by
construction, so the problem it solved doesn't exist.

```
tab strip
drives box          |  jobs in flight        <- side by side, one band
-------------------------------------------
left column (resizable)  |  menu frame
  discs -> titles        |
-------------------------------------------
status
```

- **Drives box and jobs list side by side.** Stacked they cost ~242 px of height;
  sharing a row, ~92. Height is the scarce axis — the frame is 4:3, the window 16:9 —
  so this is most of the picture budget.
- The two are separate because **a job outlives the drive**: backup finishes, the
  disc ejects, the next one goes in, and info and rip are still running on the ISO
  the previous disc left behind. Top box is hardware; the list below is work in
  flight, surviving after its drive is freed.
- **Left column is two levels**: the persistent disc list, and — on selecting a disc
  — that disc's title rows. Column is resizable against the frame.
- **No name rows along the bottom.** The picture gets full height and the full width
  right of the divider. At 1400 x 900 the frame renders roughly 893 x 670, about
  1.24x the 720 x 480 source.
- **Double-click a title row** expands it to a bigger row.
- **Each title row carries a chip per source screen.** Clicking a chip loads that
  screen in the frame and highlights the button. Rows with no button show no chip,
  which reads correctly as "nothing to look at here". The picture never moves on its
  own.
- In disc view the right side is empty. Accepted; it's the resting state.
- Open folder in Explorer: right-click on the disc row, not a button.
- **Copy the Run Remux pattern, don't refactor toward it.** No shared base class
  extracted from working shipped code on a feature branch. Share what is already
  separate: the process wrapper, log writing, keep-awake, the cancel handshake.

### Threading

- **Backup** is bound to the optical drive. One job per drive; pool size is the drive
  count, nothing to configure. Separate drives genuinely run in parallel.
- **Info and rip** both run off the mounted ISO on NVMe, competing for the same disk
  and CPU regardless of how many drives fed them. Separate pool, configurable degree.
- Open: what that degree defaults to. 1 still keeps a drive backing up the next disc
  while the previous one rips; 2 clears a stack faster if the disk holds up.
- The free-space precheck must sum across everything already running — peak is ISO +
  rips at once, ~15 GB per disc — so two jobs can't each pass a check they'd jointly
  fail.

### Preview button

Kept, repurposed as a **Plex naming-convention check**, not a dry run. It calls the
same code the transcode side already uses — title case with the acronym list,
resolution suffix after the edition tag, missing-year detection — rather than being a
second opinion about Plex that drifts from the first.

### The disc record

- **One folder per disc**, everything in it: the record and the cached frames. No
  per-title files. Fixed app-convention location, not settable.
- **Record all the data.** It is the ripping config.
- **Keep the whole menu graph**, untrimmed — trim later once it's clear what is
  unused. It is 94% of the payload: 50 KB of 53 KB, against ~3.3 KB for titles, rips
  and coverage combined. Storing it is cheap and once the ISO is deleted it is the
  only explanation of why a button bound where it did.
- **`bytes` splits in two.** MEASURED: in `menumap.json` `bytes` is TINFO **field 11**,
  the source title size on the disc. The written file's actual size is measured at
  bind time and thrown away — it survives only inside the refusal text. The record
  needs both, so the 0.95–1.00 ratio stays inspectable instead of collapsing to a
  boolean. `verified` keeps the verdict.
- **Typed names**: the app renames the file on disk. Reopening a named disc shows the
  resulting filename in the name field, not editable unless you right-click.

Shape agreed:

```
DiscRecord      Name, SourcePath, SchemaVersion, CreatedUtc, RipFolder
                Titles[], Screens[], MenuGraph (opaque)

TitleRecord     Number, Vts, VtsTitle, Angles, Chapters
                Duration, Seconds, Segments, CompositeOf[]
                FileName, Bound, Verified, SourceBytes, FileBytes
                NamedBy[] -> { Ifo, Pgc, Button, Image }
                ReachedFrom[]
                Flags -> PlayAll, NoMenuButton
                Note, Name

ScreenRecord    Ifo, Domain, Lang, Menu, Pgc, Cell
                Image, ButtonsImage, Sectors, NavPacks
                Buttons[] -> Number, Rect, Up/Down/Left/Right,
                             CommandText, Raw,
                             Resolved { Kind, Title, Vts, VtsTitle,
                                        MenuPgc, Chapters, Trace[], Notes[] }
```

---

## Open — pick up here

**Start items — do these first, in order:**

1. Cut the feature branch from `wpf-rewrite`.
2. Commit `TranscodeTools_Reference.md` and `Development_Rules.md` into `docs\`,
   and this handoff into `docs\handoffs\`.
3. Put `dvdmenumap.py` and `examples\` under `tools\oracle\`.

Once those land, files can be pulled from GitHub instead of uploaded.

**Then the next task:** write the model classes above plus a loader that hydrates them
from an existing `menumap.json`, so the view binds to real data from day one. Proposed
home: a `Ripping\` folder in the project with its own file, not `Models.cs` — that
file already carries the remux and transcode row types.

*Queue, parked:*

- **Chip ranking.** Multi-chip is not the edge case. MEASURED on `THE_CENTENNIAL`:
  title 1 and 2 have one source each; **title 3 has three** (root pgc 1, pgc 2,
  pgc 9) and **title 5 has three** (VMG pgc 3, pgc 12, pgc 15); titles 4 and 6 have
  none. Two problems follow. Three chips plus number, runtime and filename won't fit
  one line in a 280 px column — the row wraps, or shows one chip with the rest behind
  the expander. And the chips aren't equally worth clicking: title 3's pgc 2 source is
  the subtitles screen, whose button 3 is RESUME EPISODE — it reaches title 3 but says
  nothing about what title 3 is called, while pgc 9 under PLAY ALL is the useful
  picture. So the row wants a best chip up front. Cheapest ranking that would work on
  this disc is button count on the screen; whether that generalises is unknown from
  one disc.
- **Frame aspect.** The sheets render the frame at 1440 x 960, exactly 2x the stored
  raster, square pixels. 720 x 480 is NTSC DVD and displays 4:3, so the PNGs are
  slightly stretched horizontally versus a TV. Button rects in the JSON are in
  720 x 480 source coordinates, so matching the existing sheets keeps **one uniform
  scale factor for image and hotspots alike**. Correcting the aspect gives x and y
  different multipliers. Undecided; nothing in the binding cares.
- Work-pool default (1 or 2).
- Preferences panel + gated app locations.
- OCR pass: button-label OCR → keyword classification → screen ranking.
- Disc identity by fingerprint.
- Blu-ray contact-sheet generator.
- Help dialog.
- **Cheap check, never run:** `TXTDT_MGI` at VMG offset `0xD4` may hold real title
  text. Almost always empty on commercial discs; would beat OCR outright.

---

## The pipeline

```
backup → info → rip → naming
```

All four are new work. Unattended: backup, info, rip. Attended: naming only.

Analysis rides at the tail of **rip**, still unattended:
`IFO parse → VM decode → resolver → frame extraction → binding`. Because rip precedes
naming, discs arrive in the naming view **already bound** — no unbound row state, no
lazy rebind.

The ISO is deleted as the **last action**, deliberately after the ripped files have
been tested. **Delete gate:** every title bound (field 27 key, size inside the field-11
band) and frames cached. If anything refuses, the ISO stays and the disc is flagged on
its row in the disc list. Nothing is lost — the discs are all still on the shelf.

**Vocabulary:** *Rip* = `makemkvcon mkv` extracting the .mkv titles out of the mounted
ISO. Earlier notes called this "remux"; they were wrong. *Remux* and *Transcode* are
the existing TranscodeTools stages and are **unchanged** by any of this.

---

## Preferences panel

Three checkboxes — **Rip / Remux / Transcode**, all default checked. The existing
app-locations panel requires only the apps the chosen modes need; uncheck Rip and
MakeMKV stops being a requirement. This is the opt-out: a user can skip ripping
entirely and use the app only for transcoding, exactly as they can today.

---

## Paths and guards

| Path | Settable | Notes |
|---|---|---|
| **in** — the ISO | yes | one line in the disc processing window |
| **out** — the rips | yes | separate folder from in; same drive is fine |
| **disc data** | **no** | fixed location, app convention: one folder, a subfolder per disc. Reuses the existing load code. |

- In and out must be **separate folders**, never the same one. That is what keeps the
  delete step from ever operating in the folder holding the output.
- **Not on C:** is Andy's own preference, not policy. Warn, never block — community
  app, some users have one drive.
- **Free-space precheck refuses.** Show the numbers. This is the check that saves a
  two-hour import.

---

## Persistence

The disc list persists across restarts, the same way remux and transcode settings do.
A disc record must be self-sufficient once the ISO is gone: titles, runtimes,
bindings, hotspot geometry, cached frame paths.

Consequence, accepted: the record does not travel with the library. Moving the rips to
another machine leaves the frames behind. The filename is the payoff and it does
travel.

**Screen pruning must keep failing toward extra images, never a missing one.** With
the ISO deleted, cached frames are the only copy of the picture and a second pass
means going back to the optical drive.

---

## The port

### Module seams

1. IFO structures + sector reader + staging
2. VM decode (libdvdnav `vm_print_mnemonic` port)
3. PGC chain resolver
4. Frame extraction
5. MakeMKV `titles.txt` parser
6. Rip binder
7. Rename engine
8. Views

1–3 are pure functions over bytes with no I/O. That is what makes them portable and
testable; if the port loses that boundary it has moved the tangle rather than removed
it.

### Reuse

- **ffmpeg** — the script shells out for every image step; TranscodeTools already owns
  a process wrapper. Largest reuse win, and it defuses the riskiest-sounding part.
- **Settings load/save** — carries the disc data folder.
- ISO mount is PowerShell `Shell.Application` in the script (elevation-free, finds the
  letter by diffing the drive set). Port as a native `VirtualDisk` call or keep the
  shell-out; ~40 lines either way.

### Reporting

Every module needs a status/progress channel in its signature (`IProgress<T>` or an
observable collection). The Python prints to stdout; ported literally that becomes
`Console.WriteLine` in a GUI app — output that vanishes. Cheap in the shape from the
start, painful to retrofit through a finished resolver.

### Acceptance

`--selftest` covers the decoder, resolver and rip matcher and does **not** survive the
port for free. Keep the Python alive as an oracle under `tools\oracle\`: run both
against the same disc and diff the JSON. `THE_CENTENNIAL` has a known-good run to diff
against. Python retires when the diff is clean, not when the code compiles.

---

## Facts that must survive the port

### The join

- **Key = `TINFO` field 27** (output filename). It carries MakeMKV's title index.
- **Guard = `TINFO` field 11.** The file must land between **0.95 and 1.00** of it.
  Measured 0.978–0.981 across 9.5 MB to 7.8 GB, spread 0.12%. Field 11 is the *source
  title size on the disc*, never the size of the file written — an exact-byte build
  bound 0 of 6 and refused everything, correctly.
- The guard catches a `--minlength` desync between the info run and the rip, which
  otherwise puts the right name on the wrong title.
- **Not** the row index (`minlength` shifts it). **Not** the chapter count — MakeMKV
  strips the trailing menu-jump cell, so its counts run one short. Gate accepts
  `chapters == TT_SRPT` or `TT_SRPT - 1`.
- **Play-all detection from data alone:** multi-range segment map (field 26, e.g.
  `1-5,6-9`) plus runtime equal to the sum of the titles it spans. Flag, don't hide.
- Unmatched titles: the **actual file on disk** gets `_unmatched_` prepended.
  Idempotent.

### MakeMKV robot mode — measured

- A successful `mkv` run names no files. Per-title output is `MSG:3028`, then
  `MSG:5005 "N titles saved"`. Filenames appear only in failure messages
  (`MSG:5003`, `MSG:2019`).
- Index k ↔ `_t0k` suffix confirmed.
- `makemkvcon64.exe` is at `C:\Program Files (x86)\MakeMKV` and is **not on PATH**.
- MakeMKV does **not** create the destination folder. Without it, every title fails
  with "OS error - The system cannot find the path specified".
- The info run and the rip must use the **same `--minlength`**.

### OCR — measured, Tesseract 5.3.4, ~30 preprocessing combinations

- Decorative captions: unusable. `SIMOnivanheyRocks`, `2. Heyy ellowgA pron`.
- Plain UI text: clean on every screen. `PLAY ALL / EPISODE INDEX / SUBTITLES`.
- Key on **button labels**, not banners. "EPISODE INDEX" linking to `LinkPGCN 9` is
  what proves pgc 9 is the episode list.
- Manual labelling stands. OCR is an assist, never the mechanism.

### Proven working in the Python

IFO parsing (menu PGCI_UT walk, language units, every PGC including cell-less ones,
cell playback tables, program maps, TT_SRPT) · PGC command tables at PGC `+0xE4` ·
full VM decode, all seven instruction types, all five `if` prefixes · PGC chain
resolver with 16 GPRMs + SPRMs, `LinkPGCN`, `LinkTailPGC`, `LinkGoUp/Next/PrevPGC`,
`JumpSS`/`CallSS`, cycle detection on (domain, pgc, block, line) · ISO mounting · rip
binding 6/6 on the real disc.

---

## Closed doors — do not reopen without new information

- **`--decrypt backup`** is the answer for both menu info and rips. One optical pass.
- **`libdvdcss`** rejected — binary dependency, legal grey area.
- **TVDB** for episode names: dead. v4 needs a per-project key plus a paid subscriber
  PIN, and it cannot supply the disc-title→episode binding anyway.
- **Per-title output folders:** rejected as overengineering. The `_t0N` filename
  already carries the index.
- **Exact-byte join, join on field 24:** both wrong, see above.
- **DVD only.** Blu-ray menus are BD-J and out of scope for menu parsing.
- **Tkinter or any standalone naming UI:** dead. The UI is a TranscodeTools tab.
- **The `names.csv` round trip:** dead. Outputs are collections the views bind to,
  not files.
- **Non-modal backup popup:** dead, replaced by the tab.

---

## Reference

- Offsets and opcodes come from libdvdread `ifo_types.h` / `nav_types.h` and libdvdnav
  `vm/vmcmd.c`. **Fetch the source when extending — do not work from memory.**
- Drive `D:` = MakeMKV `disc:1`; drive 0 = `E:`; mounted ISO showed as `J:`.
- **Drive vs backup:** IFO parsing, command tables, resolver, coverage and join all
  work straight off the optical drive — CSS scrambles VOB payload, not IFOs or NAV
  packs. VMGM menu frames render off the drive; **VTS-domain frames do not**, and the
  episode index is VTS-domain. That is the only reason the backup exists.
- Test disc `THE_CENTENNIAL`: 6 titles, 2 with menu buttons, 6/6 bound. Its
  `menumap.json`: 6 titles, 35 menu PGCs across 5 IFOs, 26 screens of which 6 carry
  images, 6 button sets, 17 buttons.
- **Bugs fixed, do not reintroduce:** NAV pack detection must validate both PES
  headers, not just `00 00 01 BA` · `btn_ns` is at `hl_gi+0x11`, not `+0x13` · staging
  needs sector-aligned copy with zero-fill · don't abort on consecutive unreadable
  sectors, only if nothing reads in the first 512 · font lookup fell back silently to
  a tiny bitmap font in early builds.
