# movie_namer - Session Handoff

**Date:** 2026-10-11 (supersedes 2026-10-09)
**Home:** TranscodeTools - `C:\Users\andy\source\repos\Transcode-Tools`, branch `feature/ripping`.
Last commit `0f3d208`. The three 10-09 zips (layout, inline naming, DataGrid) are applied in the
working tree, still untested and uncommitted; `MOVIE_NAMER_SESSION_HANDOFF_2026-10-09.md` belongs in
`docs\handoffs\` with them.

---

## Pick up here

**First thing next session: test step 1** (title table from disk + disabled-button look) on the
current layout with the existing THE_CENTENNIAL data. Nothing else starts until it passes.

## Delivery

Changed files are now sent one at a time, each with its full path from the repo root. No zips.
`Development_Rules.md` updated to match.

## Step plan - serial, each tested before the next

1. **Title table from disk + disabled-button look.** Sent today, untested. Test on the current
   layout with the existing THE_CENTENNIAL data. Do not clear the Destination before this passes.
2. **Shared exclusion list** - one list, checked at every folder level Remux/Transcode/Rip read:
   ```
   "Remux",
   "Transcode",
   "Logs",
   "Completed",
   "Menu",
   ```
   New file `TranscodeTools\FolderExclusions.cs`, a case-insensitive `HashSet<string>` with
   `IsExcluded(name)`, one name per line. Replaces the separate checks in `LoadMovieFolders`,
   `ApplySubfolderTitleCase` and `RunRemux.PopulateFolderTree`. Fixed in code, not a preference.
   Before/after test: Remux Input at the current `Rip\` shows THE_CENTENNIAL as a TV show with a
   "Menu" season today, a movie after.
3. **Folder layout.** Clear the Destination first. Then full chain from Backup.
4. **disc.json as state.** Full chain again.

## Step 1 - what changed

- **Title list built from the files on disk.** One row per `.mkv` in the rip folder. A file a title
  is bound to gets #, Runtime and the menu screens; a file no title claims shows # blank and no
  screen. A disc title with no file gets no row and stays on the record.
- **Name is editable on every row unless Delete is ticked.** Clicking Name on a ticked row puts
  "Untick Delete to edit the name" on the status line; the Name cell's tooltip says the same.
- **Applied names no longer lock.** Right-click Edit Name is gone; Play in MPV is the only menu item.
- A named file shows its name in the Name cell. A file that still has its MakeMKV name or the
  `_unmatched_` prefix starts empty. Naming an `_unmatched_` file drops the prefix.
- **Play-all** starts with Delete ticked only if it has never been named.
- **Deleted files:** the row leaves the list on Apply. The title stays in `disc.json` with
  `Flags.Deleted` set and no file.
- **ISO after naming:** deleted once no file carries the `_unmatched_` prefix and every file a title
  is bound to has that title's name applied.
- **`FlatButton` disabled look:** opacity 0.4, arrow cursor. `AccentButton` inherits it, so Apply,
  the drive row's Backup and Remux View Command all show it.

Files: `TranscodeTools\Ripping\TitleNaming.cs`, `TranscodeTools\Ripping\RipView.xaml`,
`TranscodeTools\Ripping\RipView.xaml.cs`, `TranscodeTools\Themes\ThemeResources.xaml`,
`docs\Development_Rules.md`, this handoff.

## Decided this session

- **Theme:** the disabled look on `FlatButton` is added. Andy changed his 09-18 stance after seeing it.
- **Files renamed outside the app:** no special handling yet. They show as files no title claims.
  Andy wants to feel the flow first.
- **Settings file:** `%AppData%\TranscodeTools\settings.json`, JSON. There since the first WPF commit
  (`8e98ec0`, 2026-03-12). Recorded here because it was never written down.

### Folder layout (step 3)

```
<Destination>\
    <disc folder>\           renamed to the Plex name later
        <DISC>.iso           loose in the folder
        *.mkv
        Menu\                disc.json + menu pictures
        (hidden reserve file while a backup or rip runs)
    Logs\...                 unchanged for now
```

- `ISO\`, `Rip\`, `Reserve\` go. (`Discs\` is left over from the 09-18 layout; delete it.)
- Discs are found by `*\Menu\disc.json`, skipping the shared exclusion list.
- A disc is identified by the volume label in `disc.json`, not its folder name, so renaming the folder
  breaks nothing and inserting the same disc again still finds it.
- The jobs list still rebuilds from the backup log plus whether the ISO exists (only the ISO's
  location changes in step 3).
- No migration code; Andy clears the Destination and re-runs THE_CENTENNIAL.

### disc.json as state (step 4)

- `disc.json` is created when Backup starts and records the stage: backed up, info, ripped, named.
- The jobs list is rebuilt from `disc.json` plus whether the ISO is there.
- A backup redo **recreates** the record: a fresh one carrying over only the volume label and the
  folder-name history.
- **Folder-name history** in `disc.json`: every name the folder has had, with the date. A record found
  in a folder whose name doesn't match the last entry was renamed outside the app - Andy has an idea
  for handling that.
- The backup log only drives the "processed before" prompt.

## Not settled / parked

- **Logs:** undecided. Rip always writes four kinds (`Logs\Backup\<DISC>.log`,
  `Logs\<timestamp>\<DISC>\info.log`, `rip.log`, `naming.log`) and ignores Write Log Files. Andy wants
  it to follow the Remux/Transcode rules eventually but feels it's excessive; decide after seeing more
  than one disc.
- **Remux auto-move** of disc folders into `Completed\`: not decided.
- **TV box sets:** parked. Too many disc configurations; needs its own back-and-forth.
- **99-title discs** (Disney style): Rip uses `all`, so near-full-length padding titles would all be
  written; the 1.25 x ISO reservation would refuse or run out. Circle back later.
- Earlier queue unchanged: log management, Preferences mode checkboxes + job cap, auto-start, disc
  fingerprint, Blu-ray contact sheet, Help dialog. Known: closing the app mid backup/rip leaves
  makemkvcon running; a hand-mounted ISO shows in the drives box; Destination not reloaded at launch.
- `TranscodeTools_Reference.md` still has no Rip section (stale since 09-15).

## Working agreement (Andy, today)

- When a consequence is already worked out, state it ("X will need updating for Y") so Andy can just
  agree. When re-asking a missed question, just ask again.
- Read the whole solution at session start (done today: every file, plus both docs and the handoff).
