# movie_namer - Session Handoff, part 2

**Date:** 2026-10-20, evening (follows `MOVIE_NAMER_SESSION_HANDOFF_2026-10-20.md`; read both)
**Home:** TranscodeTools - `C:\Users\andy\source\repos\Transcode-Tools`, branch `feature/ripping`.
Everything from this session is in the commit made at the end of it.

---

## Pick up here

**Put one or two more discs through the whole chain** before starting the language work. Use a
regular movie with extras, ideally from a different studio than Disney. Tonight's resolver and
naming rule were shaped by SB30NNW1 and checked only against it and THE_CENTENNIAL. The new disc
shows whether the one-item rule (below) holds up away from Disney's Play / Fast Play / Play All
design. Bring the logs or a screenshot from wherever it stops or looks wrong.

## How a session runs

Unchanged from part 1:

- The repo folder is connected and read straight from the working tree.
- Git stays with Andy.
- Delete permission is asked for per session.

New tonight:

- **`F:\rips` is needed as well** as the repo. It holds the ISOs, the logs and each disc's
  `disc.json`.
- **Probes against the ISO run in Python on Andy's machine**, reading the ISO's file table
  directly (no mount). `tools\oracle\dvdmenumap.py` supplies the command decoder for those
  probes.
- **The compile check is run in the cloud container**, using a .NET 9 SDK installed there. The
  resolver can also be run there against a disc's real IFOs plus its `disc.json`, before
  anything is installed. That means copying the IFOs over, which tonight went through a scratch
  folder on `F:\rips`, deleted at the end.

## Built this session

### MakeMKV's skipped duplicates (`SameAs`)
- **The problem.** MakeMKV drops titles that are identical to another, and says so in
  `MSG:3027` ("Title #36 in VTS 10 is equal to title #35 and was skipped"). Menu buttons often
  name the skipped copy, so the file MakeMKV kept never got that button's screen.
- **`MakeMkvInfo.ParseSkipped`** reads those lines. `TitleRecord.SameAs` holds the kept title.
  A 3027 line that can't be read is reported, not dropped. `SameAs` is cleared at the start of
  every Info run.
- **Where it shows.**
  - `info.log` says "title 36: skipped by MakeMKV, same as title 35".
  - The binder's not-bound reason says "MakeMKV skipped it: same as title 35".
- **Measured on SB30NNW1.** 16 titles skipped. The binding was identical across three runs:
  17 of 59 bound, 27 files, the same pairs.

### Resolver: the disc's program is run, not just followed to the first title
- **Start-up.** The first-play chain (`FP_PGC`) is run once, until it settles on a menu screen.
  Every button then starts from the registers it left, plus `s[8]` (the button being pressed).
  - The path is saved as `DiscRecord.StartupTrace`.
  - A start-up that reaches no menu falls back to the player defaults, and says why.
- **Menu PGCs.** A menu PGC is a stopping point only if the button scan found buttons on one of
  its cells, or a cell is an infinite still. Anything else (warnings, logos) plays through:
  cells, then post-commands.
- **Titles are played.**
  - Pre-commands, cells with their cell commands, then post-commands.
  - `CallSS` out to a menu saves a resume point, and `RSM` returns to it.
  - Chapter jumps start at the chapter's program.
  - Post-commands with no link carry on to the next PGC, or end playback.
- **`ResolvedTarget.Played`** lists every title the chain played, in order. `Title` is the
  first.
- **Loop detection includes the registers.** The same place reached with different registers is
  a new visit, not a loop. This was needed because every title on SB30NNW1 returns through the
  same dispatcher.
- **`Ifo.cs` now reads:**
  - title PGCs (`VTS_PGCIT`);
  - the chapter table (`VTS_PTT_SRPT`);
  - the first-play PGC;
  - program maps;
  - each PGC's playback time.
- **`TitleRecord.PlaySeconds`** is filled at Analyse from the IFO. It is null on records made
  before tonight, and ↺ rewrites it.
- **Measured on SB30NNW1:**
  - The start-up runs FP → VTS 14 video → VMGM 11 → VMGM 2 → VMGM 5 → VTS 2, settling on the
    VTS 2 pgc 4 language screen.
  - Play now runs titles 7, 8, 4, 10, **1**, 11, 2 instead of stopping at `RSM`.
  - Fast Play runs the start-up clips, the movie, then the extras.
- **Measured on THE_CENTENNIAL:** all 17 buttons resolve as before.

### Which buttons name a title (the one-item rule)
- **An item** is a title that some button's chain plays first, whose `PlaySeconds` is not 0.
  Skipped copies count as the title they copy.
- **A button names a title** only when its chain plays exactly one item.
- `TitleCoverage.NamingButtons` applies the rule when a title's screens are shown. It runs then,
  rather than at Analyse, because `SameAs` only exists after Info. `NamedBy` stays as the raw
  "this button's chain plays it".
- **Result on SB30NNW1:**
  - The movie gets main menu Play, all 8 Scene Selection pages, and Backstage Disney buttons 2
    and 3.
  - Each extra and each trailer gets only its own button.
  - Fast Play, the language screen and Play All name nothing, because each plays several items.
  - `A1_t02` (title 5) and `A1_t04` (title 8) get no screens. Neither is ever played on its own
    by any button; only the VMGM pgc 6 switchboard reaches them.

### Chip strip
- The strip row is 46 high, so its horizontal scroll bar sits under the chips instead of cutting
  them in half. The picture doesn't move.

## Files changed (all under `TranscodeTools\Ripping\`)

| File | Change |
| --- | --- |
| `DiscRecord.cs` | `SameAs`, `PlaySeconds`, `StartupTrace`, `ResolvedTarget.Played` |
| `MakeMkvInfo.cs` | `ParseSkipped`; `SameAs` set in `Apply` |
| `RipBinder.cs` | Not-bound reason for skipped titles |
| `Ifo.cs` | `IfoPgc` base class; title PGCs, chapters, first play, program map, playback time |
| `Resolver.cs` | Start-up run, title playback, buttons-or-still menu test, `Played` |
| `TitleCoverage.cs` | `NamedBy` from `Played`; `NamingButtons` (the one-item rule) |
| `RipJob.cs` | `FillPlaySeconds`; screens and button number passed to the resolver |
| `RipView.xaml.cs` | Chips from `NamingButtons` |
| `RipView.xaml` | Chip strip height |

## Decided this session

- **Skipped copies.** A button that names a title MakeMKV skipped shows on the kept title's
  file. The record keeps what each source said (`SameAs`); the joining happens when the screens
  are shown.
- **Buttons with no title.** Titles played only by the disc's own program, not by a button, are
  interstitials and not endpoints. They get no screens.
- **What decides the screens is measured, not a rule of thumb.** "Last title in the chain" was
  rejected: it lands on title 2, a 0-second end title. "Longest title" was offered and not taken.
  The one-item rule was agreed.
- **`F:\rips\_scratch`** was deleted at the end of the session.
- **Language: use the disc's own data, not the pictures.** OCR was rejected. The disc's commands
  say which menu cell is which language.
- **Preferences gets one language option:** "Show English menu screens only". Its default is not
  settled yet.

## What was learned about SB30NNW1 (Disney, *Space Buddies*)

- **Title 10** is a 5-second title card, played only for Spanish (pgc 1) and French (pgc 3).
  Register 12 picks between them. English gets no card.
- **Language.** All three menu languages are in one `en` menu set. The disc picks the cells
  itself from register 8:
  - 0 English, cells 1–3 (main menu cells 2–3);
  - 1 French, main menu cells 5–6;
  - 2 Spanish, main menu cells 8–9.

  The language screen (VTS 2 pgc 4) ties buttons to codes: `es` highlights button 3, `fr`
  button 2, and `en` skips the screen entirely. Register 8 is set to the button number minus
  one. The movie's audio streams are `en`, `fr`, `es`, `en`.
- **Title 7** is a 0-second entry point that Play and Fast Play jump to.
- **Titles 8, 4 and 10** are start-up clips. **Title 11** is an end card, and **title 2** is
  0 seconds.
- **Fast Play** plays the movie and then the extras. **Play All** plays the trailers in a row.
- **Titles 12 and 6 fail the chapter gate:** the disc says 3 chapters, MakeMKV says 1. So
  `A1_t05` (a trailer, title 12) stays `_unmatched_`, and the Sneak Peeks button for it can't
  land. The gate was not loosened.
- **The 80 "block ended without a link" buttons are ordinary menu-to-menu navigation.** Earlier
  in the session they were floated as possibly lost titles; that was wrong.

## Step plan - serial, each tested before the next

0. **More discs through the full chain** (above).
1. **Language per menu cell.**
   - Play the disc's program once per audio language the disc lists.
   - On a language screen, press the button the disc highlights, then follow every menu button.
   - A cell reached under exactly one language gets that language, stored on the screen in
     `disc.json`.
   - Chips are labelled with the language ("pgc 22 · French"). A screen without one keeps its
     cell number.
   - Then the Preferences > Rip checkbox "Show English menu screens only". Screens with no
     language, and discs with no English copy, are always shown.
2. **Interstitial files start ticked for Delete** in naming, the way play-alls do: files whose
   title no button names. Andy: "we'll come back to that."
3. Steps 2–4 from part 1 are unchanged: the shared exclusion list, the folder layout, and
   `disc.json` as state.

## Parked queue

Unchanged from part 1:

- per-disc naming fields;
- Blu-ray / 4K;
- Mismatch / Resolve;
- the themed CheckBox;
- the `--noscan` comment;
- the carried items.

Added tonight:

- **Chip order.** The order chips appear in has not been looked at since the rule changed which
  chips there are.
- **The runtime column for `_unmatched_` files is empty**, because it comes from the bound
  title. The proposed fix is an ffprobe duration per file at rip time, stored on `RipFiles`.
  Proposed, not agreed.
- **The title table's sort order** is bound titles by number, then unmatched files by name.
  Andy asked about it; nothing has been decided.
- **The start-up assumes the player's audio and subtitle language registers start at 0.** Real
  players set them to English. So on SB30NNW1 the walk stops on the language screen, where an
  English player would skip it. The language work in step 1 sets these registers per language,
  which covers this.

## Loose ends

- **Probe leftovers in `F:\rips`:** `title10.txt`, `title10.png`, `vts07_pgc1.vob/.png` and
  `vts07_pgc3.vob/.png`. They can be deleted.
- `ffmpeg.txt` and `text_scan.txt` in the repo root are still scratch, and are left out of the
  commit.
- Computer use reported "off" on Reliant even after it was turned on. The screenshots came from
  Andy instead.
