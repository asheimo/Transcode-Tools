# movie_namer - Session Handoff

**Date:** 2026-10-20 (supersedes 2026-10-11)
**Home:** TranscodeTools - `C:\Users\andy\source\repos\Transcode-Tools`, branch `feature/ripping`.
Last commit `9050676` "Rip mode: next-step workflow, Complete/Edit, file progress, Plex types".
Everything from this session is in it, except this handoff.

---

## Pick up here

**First thing next session: run a regular movie DVD through the whole chain.** That means Backup,
Analyse, Info, Rip, naming with types, Apply, then Complete. Pick one movie with extras and no TV
episodes. THE_CENTENNIAL is a miniseries, so it can't test movie naming. Bring the logs or a
screenshot from wherever the run stops or looks wrong.

## How a session runs

- The repo folder is connected and read straight from the working tree. No zips.
- I write changed files in place and name each one with its full path.
- Git stays with Andy. I only read git state (with `--no-optional-locks`); Andy commits from Visual
  Studio, and I supply the commit message when asked.
- Delete permission on the repo folder has to be asked for in each session.
- Compile check: stage the changed files, copy them into `/tmp/claude-0/build/TranscodeTools`, then
  run `dotnet build --no-incremental -p:EnableWindowsTargeting=true`. The only expected warning is
  the existing CS7035 FileVersion warning.

## Built this session (all in `9050676`)

### Disc list (top band)
- **One disc list.** The old bottom disc list and "Back to discs" are gone. The jobs list is the
  disc list, with one row per disc record plus any completed backup whose ISO is still there.
- **Row click only selects.** The action button is what opens the titles.
- **Step shows the next step** in the chain: Analyse > Info > Rip > Naming > Complete.
  The button reads:
  - Start when the next step is Analyse
  - Continue for Info, Rip and Naming (Naming opens the titles)
  - Open when the disc is complete (titles shown read-only)
  - Cancel while running
- **Continue resumes at the shown stage.** It does not start again from the beginning.
- **Start over (↺)** sits in the same cell as the action button, and is also on the right-click
  menu. It runs the chain again from Analyse. It only shows when the job isn't running, the step is
  past Analyse, and the ISO is still there.
- **How the next step is worked out (`RipView.NextStep`):**
  - `Complete` set on the record: Complete.
  - Otherwise, `RipFiles` present and every non-deleted file on disk: Naming.
  - Otherwise, any title with a rip name: Rip.
  - Otherwise: Info.
- **"Hide complete discs"** checkbox, saved as `AppSettings.RipHideComplete`. It filters the rows
  out of view only.
- **Layout:** the top band is 150 high.
  - Row 1: Destination button (sized to its text) with the combo box, and the Hide checkbox at the
    top of the right column.
  - Row 2: drive list on the left and disc list on the right, with a splitter between.
  - The Status column is narrowed to 195.
- **Rip progress:** reads `(2/6) ripping 45% · 30% total`, or `ripping 30%` before the file count
  is known. Measured on MakeMKV 1.18.3:
  - `PRGV:current,total,max` gives both bars.
  - `MSG:5014` gives the file count (fields[5]).
  - `PRGC:5057`/`5017` give the 0-based file index.

### disc.json
- **`RipFiles`:** a list of `{ Name, Bytes, Deleted }`. It is written after binding, and it is null
  until a rip finishes. Every rename and delete in the app updates it. A deleted file stays in the
  list with `Deleted` set to true.
- **`Complete`** (bool): set only by the Complete button. Nothing else marks a disc complete.
- `DiscStore.Load(destination, discName)` loads one record.

### Naming
- Fixed: a row still counted as changed after Apply. `NamingRow.Applied` now moves the baseline
  forward.
- Fixed: a file ticked for Delete was blocking its own name. Deletes run first, so their names are
  free to take.
- **Single-click Name edit**, only in the Name cell. A click anywhere else in the row only selects
  it.
- **Type column** (between Name and Delete):
  - **Choices:** Main title, Behind the Scenes, Deleted Scene, Featurette, Interview, Scene, Short,
    Trailer, Other. These are Plex's eight suffixes, checked against Plex's support page; the match
    has to be exact.
  - **File name:** an extra is the name plus `-<suffix>.mkv`; Main title adds nothing. The Name
    cell never shows the suffix.
  - **No type field in disc.json:** the type is read back from the file name. A named file with no
    suffix is a Main title; a file that has never been named has no type.
  - **Apply refuses when:**
    - a row has a name but no type;
    - a typed name ends in a Plex suffix (pick the type instead);
    - a row has a new type and is also ticked for Delete.
  - The duplicate check uses the full file name, suffix included.
  - The "No year or episode number" list in the Apply confirm only shows Main title rows.
- **Preferences > Rip, "Extras types offered when naming":** one checkbox per type, saved as
  `AppSettings.RipHiddenExtraTypes` (the suffixes left out, empty by default). Main title is always
  offered. A file that already has a type you've since hidden still shows it. A change takes effect
  the next time a disc's titles are opened.
- **Complete / Edit:**
  - **Complete:** sits beside Apply. It is disabled while there are unapplied changes, including
    during an edit. It refuses, listing the files, while any file is unnamed (MakeMKV name,
    `_unmatched_` prefix, or a bound title with no name). Every kept file therefore has a name and
    a type.
  - **Once complete:** the titles go read-only (Name, Type and Delete disabled), and Apply reads
    **Edit**.
  - **Edit:** clears the mark and puts the disc back at Naming.
- **ISO deletion moved from Apply to Complete.** With the setting "When the disc is marked
  complete" (the enum value is still `Naming`) and the ISO present, Complete asks first. The
  dialog is the themed `ConfirmDialog` with Confirm and Cancel. The ISO is deleted before the mark
  is set, so a failed delete changes nothing. "After Rip" is unchanged: it deletes once the rip and
  binding succeed, and the row stays.

### Other
- **Empty folder pickers turn red:** Destination, Input Directory and Output Directory. A
  `MultiTrigger` in the ComboBox template (editable, read-only, Text empty) sets the theme brush
  `MissingBg` (Dark `#8B2323`, Light `#F4A6A6`). Those three are the only read-only editable combos
  in the app.
- Rip mode remembers its own window size and stays on its monitor (`MonitorWorkArea`). This was
  earlier, uncommitted work that went into this commit.
- `Development_Rules.md`: work from the repo folder, and git stays with Andy.

## Decided this session

- The step column shows the next step, not the last one done.
- Continue covers every step after Analyse. Restart is the ↺ icon, not just a right-click item.
- File progress counts **files**, not titles, because files can exist that no title claims.
- `disc.json` keeps the file list in step with what the app does to the files.
- Completion is **declared by Andy** (the Complete button), not inferred from the files.
- The ISO goes on Complete, after a confirmation, not on Apply.
- The dialog buttons are Confirm and Cancel.
- Complete discs show **Open**.
- A row starts with **no type**. Every file that isn't marked for Delete must have a type.
- Per-file naming (Type) comes before per-disc naming.

## Step plan - serial, each tested before the next

0. **Movie DVD full-chain test** (above).
1. Title table from disk and the disabled-button look: in daily use on THE_CENTENNIAL, working.
2. **Shared exclusion list** (`TranscodeTools\FolderExclusions.cs`). Unchanged from the 10-11
   handoff.
3. **Folder layout.** Unchanged from the 10-11 handoff (clear the Destination first). Now tied to
   the per-disc naming fields below, since those decide the folder name.
4. **disc.json as state.** `RipFiles` and `Complete` are the first pieces of it.

## Parked queue (in this order unless Andy reorders)

1. **Per-disc naming fields:** Title, Year, Edition (`{edition-...}`), and an ID (`{tmdb-...}` or
   `{imdb-tt...}`). They are entered once per disc and build the Main title's file name and the
   folder name, e.g. `The Movie (1999) {edition-Director's Cut} {tmdb-1234}.mkv`.
   **TV naming belongs here too.** The show title and year come from the disc, season and episode
   come per file, and it needs the `Season 01` folder. Plex recommends the year on series as well,
   e.g. `Band of Brothers (2001)/Season 01/Band of Brothers (2001) - s01e01 - Currahee.mkv`.
   Design this together with step 3.
2. **Blu-ray / 4K:** its own design. Analyse is built on DVD IFO files and DVD menus, which
   Blu-rays don't have. MakeMKV Info and Rip already handle any disc.
3. **Mismatch / Resolve:** when disc.json's file list and the disk disagree (a file changed in
   Explorer), the step shows Mismatch and the button reads Resolve. A pop-up shows the json list
   against the disk and offers:
   - mark a missing file deleted;
   - adopt a rename (paired by size);
   - add an extra file as unclaimed.
4. **Themed CheckBox applied app-wide** (option a). It needs broader testing, so it's out of scope
   for now.
5. **The `--noscan` comment in `MakeMkvRip.cs`:** the rip log contradicts it, because drives are
   still listed.
6. Carried from 10-11:
   - **Logs:** undecided.
   - **Remux auto-move** into `Completed\`: undecided.
   - **99-title discs.**
   - **TV box sets.**
   - **Older queue:** Preferences mode checkboxes and job cap, auto-start, disc fingerprint,
     Blu-ray contact sheet, Help dialog.
   - **Known issues:** makemkvcon keeps running if the app closes mid-run; a hand-mounted ISO
     shows in the drives box; the Destination isn't reloaded at launch.
   - **`TranscodeTools_Reference.md`** still has no Rip section.

## Loose ends

- `ffmpeg.txt` and `text_scan.txt` in the repo root are scratch dumps and were left out of the
  commit.
- THE_CENTENNIAL's ISO is still at `F:\rips\ISO\THE_CENTENNIAL.iso`, in the old layout.
