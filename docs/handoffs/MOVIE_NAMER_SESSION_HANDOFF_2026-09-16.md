# movie_namer — Session Handoff

**Date:** 2026-09-16 (supersedes 2026-09-15)
**Status:** the design questions on the open list are all answered. Still no C# written.
**Home:** TranscodeTools (WPF, C#, .NET 9) — `C:\Users\andy\source\repos\Transcode-Tools`, solution `Transcode Tools.sln` at the repo root, project in `TranscodeTools\`.
**Reference only:** `dvdmenumap.py` (~2100 lines), previously `C:\temp\Claude\movie_namer\`.

---

## Decided 2026-09-16

### Rip is a mode, not a tab — supersedes the 2026-09-15 "Ripping tab"

- MEASURED: the main window has **no tab control**. It has the "User Mode:" radio
  switcher (Remux / Transcode) that swaps the whole content area.
- Ripping becomes a **third radio button, labelled "Rip", placed leftmost**. Same
  design as the existing two.
- The app **opens on the leftmost mode button that is showing**, detected from what is
  available. Today `RemuxRadio` is `IsChecked="True"` in the XAML; that changes.
- The code tracks mode as one bool, `_isTranscodeMode`, read from `TranscodeRadio`. It
  becomes a three-way value.
- Once the Rip / Remux / Transcode preferences exist, an unchosen mode loses its button.
- Rip mode's content is its own control, **`Ripping\RipView.xaml`**, shown by the main
  window when Rip is selected. MEASURED: `MainWindow.xaml` is ~52 KB and
  `MainWindow.xaml.cs` ~137 KB, the largest files in the project; the new layout does
  not go into them.

### Layout

```
User Mode:  (Rip) (Remux) (Transcode)
[Destination] [history dropdown..............................]   <- full width
drives box                  |  jobs in flight
--------------------------------------------------------------
left column (270, resizable) |  chip strip (fixed height)
  discs -> disc titles       |  menu frame
--------------------------------------------------------------
status
```

- **Vocabulary:** a *disc title* is a title row on the disc. It is kept separate from
  the *name* typed for it.
- **Destination line** runs full width above the drives/jobs band. It starts with the
  existing picker design — 130 px button plus history dropdown in a 32 px row, as
  Input/Output Directory do — and gets revisited once seen in the product. Andy's
  call, not set in stone.
- **Left column** defaults to **270 px**, resizable. Disc list; selecting a disc shows
  its disc titles.
- **A disc title row is one line:** number, runtime, filename. Chips are no longer on
  the row.
- **Chip strip above the picture.** Shows the selected disc title's source screens, one
  chip per screen, **in the order the screens were found** — no ranking. The strip
  **always keeps its height**: one screen still shows one chip, no screens shows an
  empty strip, so the picture never moves between rows. Clicking a chip loads that
  screen and highlights the button.
- **Picture at original stored size** — the 720 x 480 raster, square pixels, **no 4:3
  correction**. When space is short it shrinks with its aspect ratio kept; it is never
  enlarged. The picture and the hotspot layer sit in one container, so button rects
  stay in source coordinates.
- MEASURED widths at the default 1000 px window: ~984 client − 24 margins − 8 splitter
  − 270 column − 2 border ≈ **680 px, ~94%** — accepted. Height is a rough estimate
  from paddings: ~535 px available before the Destination line; with it the picture
  lands near 98%. Not rendered yet.
- The old "1400 x 900 renders 893 x 670" figure is dead — the picture caps at 720 x 480.
- Carried from 2026-09-15 and still standing: drives box and jobs list side by side (a
  job outlives its drive); double-click expands a disc title row; the right side is
  empty in disc view; open folder in Explorer is a right-click on the disc row; copy
  the Run Remux pattern rather than refactoring toward it.
- Fact kept in case 4:3 correction is ever reopened: each menu domain records its own
  display shape — `vmgm_video_attr` in the VMG IFO, `vtsm_video_attr` in each VTS IFO,
  2-bit `display_aspect_ratio` (0 = 4:3, 3 = 16:9), plus NTSC/PAL and picture size
  (libdvdread `ifo_types.h`). Not used.

### Destination — supersedes the two settable paths

- **One settable folder**, with a history list like Input and Output. Most users will
  pick the same path every time.
- Inside it the app keeps:
  - `ISO\<DISC>.iso`
  - `Rip\<DISC>\` — that disc's .mkv files
  - `Reserve\` — hidden, see below
- **Everything the app writes lives under Destination.** Nothing sits outside it.
- The in/out separation rule now holds by construction; no check needed.
- The app **creates `Rip\<DISC>\` before each rip** — MakeMKV won't.
- `<DISC>` comes from the volume label. Same-label collisions are not pressing: box
  sets, usually TV, are labelled by season. Disc fingerprint stays parked.

### Jobs and disk space

- **Backup:** one job per drive (unchanged).
- **Info and rip: no default limit.** Each ISO starts info and rip as soon as its backup
  finishes. A preference caps the count; it belongs with the preferences panel.
- **Reservation:** each backup reserves **disc size x 2** from the start — its ISO plus
  its rips. That is the enforcement. No resizing after the info run.
- **Accounting:** a job starts only if
  *free space now − what every running job still needs ≥ what this job needs*, where
  *still needs* = reservation − bytes written so far. Checks run one at a time, so two
  jobs can't each pass a check they would jointly fail.
- **Space held on disk:** each running job has a reserve file in `Reserve\`, opened with
  a pre-allocation size (.NET `FileStreamOptions.PreallocationSize` — NTFS takes the
  space immediately, nothing written) and delete-on-close.
  - As the job's real output grows, the reserve file shrinks by the same amount.
  - At job end — success, failure or cancel — the app closes it and Windows deletes it.
  - If the app crashes or is killed, Windows still deletes it.
  - A power cut or bluescreen leaves it behind, so **`Reserve\` is swept at startup**.
- Checked 2026-09-16: Windows' own "Reserved storage" belongs to the OS (updates), is
  system-wide and admin-controlled. There is no per-app reservation without a file.
- **Short on space:** the job waits, and its row shows the numbers ("waiting for space:
  needs X GB, Y GB free"). **Refused** only if the disc can't fit with nothing else
  running.
- **Over the reservation:** if a disc needs more than 2x (a play-all title duplicates
  its footage), the rip carries on into ordinary free space; if that runs out, it fails
  visibly.
- **Every failure is obvious.** The job's row turns red and shows the program's own
  message, whatever the cause — disk space is not the only one. A failed backup leaves
  the disc flagged; a failed rip keeps its ISO (delete gate).

### Code layout

- MEASURED: the project is flat; the only subfolder is `Themes\`.
- **`TranscodeTools\Ripping\`** holds the whole feature: the model classes first, then
  the port modules as they land, and `RipView`.
- Namespace stays plain **`TranscodeTools`**, not Visual Studio's folder default
  `TranscodeTools.Ripping`, so existing code uses the new classes without an extra
  `using`.

---

## Decided 2026-09-15 — still standing

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
  describes the app as remux and transcode only — the four-stage chain, Rip mode, the
  port and the Rip/Remux/Transcode preference panel appear nowhere.
- The repo `README.md` still tells users the transcode side wraps other-transcode,
  which the reference doc records as removed at Step 2.

### Build order

**View first.** The rough naming view comes before porting the decoder, resolver and
binder — there has to be a view to test the engine from.

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
   and **this** handoff (2026-09-16) into `docs\handoffs\`.
3. Put `dvdmenumap.py` and `examples\` under `tools\oracle\`.

Once those land, files can be pulled from GitHub instead of uploaded.

**Then the next task:** write the model classes above plus a loader that hydrates them
from an existing `menumap.json`, so the view binds to real data from day one. Home:
**`TranscodeTools\Ripping\`**, namespace `TranscodeTools`.

**Open design detail — does not block the next task:** what an expanded disc title row
shows, now that chips live above the picture — including where the name is typed.
Settle when the view is built.

**To measure on Andy's hardware:**

- What size Windows reports for a disc's drive letter before anything is read. The
  backup reservation (disc size x 2) depends on it.
- Whether MakeMKV's backup file grows on disk as it writes. Shrinking the reserve file
  depends on watching that growth; if the file only appears at the end, use MakeMKV's
  progress output instead.

*Queue, parked:*

- Preferences panel + gated app locations + the info/rip job cap.
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
entirely and use the app only for transcoding, exactly as they can today. An unchecked
mode also loses its User Mode button, and the app opens on the leftmost one left.

The info/rip job cap lives here too. A "Rip" tab would match the existing Remux and
Encode tabs.

---

## Paths and guards

| Path | Settable | Notes |
|---|---|---|
| **Destination** | yes | one line, full width, history list. Holds `ISO\`, `Rip\`, hidden `Reserve\` |
| **disc data** | **no** | fixed location, app convention: one folder, a subfolder per disc. Reuses the existing load code. |

- ISO and rips are always separate folders under Destination, so the delete step can
  never operate in the folder holding the output.
- **Not on C:** is Andy's own preference, not policy. Warn, never block — community
  app, some users have one drive.
- **Free space:** reservation of disc size x 2 per backup, held on disk; wait when
  short, refuse only when the disc can't fit on an empty drive. Show the numbers.
  Full rules under *Jobs and disk space* above.

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
it. All modules live under `Ripping\`.

### Reuse

- **ffmpeg** — the script shells out for every image step; TranscodeTools already owns
  a process wrapper. Largest reuse win, and it defuses the riskiest-sounding part.
- **Settings load/save** — carries the disc data folder and the Destination history.
- **Directory history** — Destination reuses the Input/Output history code and the
  history-size preference.
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
- **Tkinter or any standalone naming UI:** dead. The UI lives in TranscodeTools.
- **The `names.csv` round trip:** dead. Outputs are collections the views bind to,
  not files.
- **Non-modal backup popup:** dead.
- **A real tab control for ripping:** dead. Rip is a User Mode button (2026-09-16).
- **4:3 aspect correction of the menu frame:** dead. Original size, shrink-only.
- **Name rows along the bottom / chips on the disc title row:** dead. Chips sit in a
  strip above the picture.
- **Chip ranking:** dead. Order found.
- **Two settable paths (ISO in, rips out):** dead. One Destination.

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
  images, 6 button sets, 17 buttons. Source screens per title: 1 and 2 have one each,
  3 and 5 have three each, 4 and 6 have none.
- **Bugs fixed, do not reintroduce:** NAV pack detection must validate both PES
  headers, not just `00 00 01 BA` · `btn_ns` is at `hl_gi+0x11`, not `+0x13` · staging
  needs sector-aligned copy with zero-fill · don't abort on consecutive unreadable
  sectors, only if nothing reads in the first 512 · font lookup fell back silently to
  a tiny bitmap font in early builds.
