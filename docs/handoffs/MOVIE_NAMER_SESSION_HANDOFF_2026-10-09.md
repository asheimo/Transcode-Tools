# movie_namer - Session Handoff

**Date:** 2026-10-09 (supersedes 2026-10-08)
**Home:** TranscodeTools - `C:\Users\andy\source\repos\Transcode-Tools`, branch `feature/ripping`.
Last commit `0f3d208` (rules line + 10-08 handoff), before it `a616039` (Info/Rip/binder, tested).
**Nothing from this session is committed or tested.** Three zips were handed over, in order:

1. `TranscodeTools-Rip_bottom_list_layout.v1.zip` - new Rip layout
2. `TranscodeTools-Rip_inline_naming.v1.zip` - naming in the title list, ISO setting, dropdown fix
3. `TranscodeTools-Rip_title_datagrid.v1.zip` - title list as a DataGrid, one Apply

Each later zip goes on top of the earlier ones (a file in a later zip replaces the earlier copy).

---

## Pick up here

1. **Answer the open design question** (below): build the title table from the files on disk.
   Andy's direction; Claude's proposal is written; Andy had not said go yet.
2. **Answer:** disabled-button look (below).
3. Claude makes those changes, then **Andy tests everything from today** in one pass.

### Open question 1 - title table built from disk (Andy's direction, 17:00)

Andy: building the table from the IFO is "always prone to error"; build it from what is on disk,
with the IFO providing the extra information. Claude's proposal:

- One row per `.mkv` actually in `Rip\<DISC>\`, `_unmatched_` files included. A deleted file
  leaves the list.
- A file bound to a title gets # and Runtime from the record, and selecting it shows that title's
  menu screen (the IFO is still what links a file to the menu buttons - without it no chips,
  picture or highlight; it also feeds the binder's chapter check).
- An unmatched file: # blank, no screen.
- **Name editable on every row unless Delete is ticked** (Andy's rule, 16:54). Clicking the Name
  cell on a ticked row: status line "Untick Delete to edit the name", same text as the tooltip.
- Applied names no longer lock; right-click **Edit Name** goes away. Renaming an `_unmatched_`
  file drops the prefix.
- A disc title with no file gets no row (nothing to name); it stays on the record.

### Open question 2 - disabled buttons

Andy: "in the current theme it is impossible to tell between greyed out and not." Cause:
`FlatButton` in `Themes\ThemeResources.xaml` has no IsEnabled=False trigger, and `AccentButton`
is based on it. Proposed: disabled = Opacity 0.4, arrow cursor (CheckBox already uses 0.35).
Also fixes the drive row's Backup (no disc) and Remux View Command. Not answered yet.

---

## Built this session (untested)

**Zip 1 - layout** (`RipView.xaml/.cs`, `MainWindow.xaml.cs`, `AppSettings.cs`, new
`MonitorWorkArea.cs`, `docs\TranscodeTools_Reference.md`)
- Rip view is one column: Destination / drives|jobs band / splitter / chip strip + picture /
  splitter / disc-or-title list / status. The left column is gone.
- Picture scales up or down to its space. **Fit: Width / Window** switch at the right of the chip
  strip (Width = fill width, scroll when taller; Window = always whole). Saved (`RipPictureFit`).
- Rip window size is remembered (`RipWindowWidth/Height`, default 1000 x 1150), clamped to the
  monitor the window is on. Remux/Transcode stay at the XAML's 1000 x 820, not remembered.
- Backlog line added: how Remux/Transcode behave when resized.

**Zip 2 - naming** (`TitleNaming.cs` new, `DiscRecord.cs`, `RipView.xaml/.cs`, `AppSettings.cs`,
`MainWindow.xaml.cs`, `UserPreferences.xaml/.cs`, `ThemeResources.xaml`)
- Preferences **Rip** tab: delete the backup ISO After Rip / After names are applied
  (`IsoDeleteAfter`, default Naming). After Rip deletes it after the record is saved (logged in
  rip.log). After Naming deletes it when every `.mkv` in the rip folder carries a user name.
  The job row goes with the ISO.
- Record: `RunLogFolder` (naming.log goes beside info.log/rip.log; old records get a new folder
  on first Apply), `Flags.Deleted`.
- Apply refused while that disc's Start is running. A job finishing for another disc refreshes
  only that disc's entry, so names being typed are kept.
- Opening a disc selects the first title (the 09-21 commit message claimed this; the code never
  did it).
- Play in MPV on the row's right-click. `YearPattern`, `EpisodePattern`, `TryLaunch` -> internal.
- **Folder dropdowns open on a click anywhere in the box** (Destination, Input, Output):
  read-only editable ComboBox's text part passes clicks to the toggle.

**Zip 3 - DataGrid** (`TitleNaming.cs`, `RipView.xaml/.cs`, `ThemeResources.xaml`)
- Title list is a DataGrid: # | Runtime | File | Name | Delete. Name edits in the cell (click/F2,
  Enter keeps, Esc undoes). Runtime and Delete centred, fixed width (`CanUserResize=False`).
- **One Apply** at the right of the list header: enabled while there is a change (and during an
  edit). Checks every changed row first (illegal chars, trailing space/dot, name already in the
  folder, named + Delete, same name typed twice) - any problem stops it with a list. One confirm
  "Rename N files and delete N?", listing names with no year/episode. Deletes first, then renames;
  stops at the first failure.
- **First DataGrid in the app.** Implicit DataGrid / DataGridColumnHeader / DataGridRow /
  DataGridCell styles in `ThemeResources.xaml`, matching the ListView look, light and dark.
  Header and cell have full templates (Aero2's header chrome ignores our colours; its cell has no
  padding); part names from Aero2.NormalColor.xaml, dotnet/wpf release/8.0. Named
  `DataGridEditingTextBox` style for the editing box.

## Decided this session

- OCR dropped: not worth it (movie names aren't on buttons; Plex matches TV on S01E01).
- Name lookup: **TMDB** over IMDb (IMDb API is paid via AWS; TMDB free non-commercial with a
  credit). Credit goes in the About box. Queued after naming; for the folder rename and main title.
- Naming in the title list, no popup. Name after File. Delete column kept. One Apply, above the
  list. Play in MPV on right-click. Unmatched files: Delete only (superseded by question 1 if
  accepted).
- Layout as built in zip 1; Fit Width/Window as a two-position switch; no SD-native comparison build.
- Rip window remembers its size; Remux/Transcode locked at 1000 x 820 for now (backlog).

## Not settled / parked

- Disc folder rename (`Rip\<DISC>\` -> movie name) - Andy thinking; TMDB may feed it.
- Queue as before: log management, Preferences mode checkboxes + job cap, auto-start, disc
  fingerprint, Blu-ray contact sheet, Help dialog.
- Known, not fixed: closing the app during a backup/rip leaves makemkvcon running; a hand-mounted
  ISO shows in the drives box; Destination not reloaded at launch; a redo deletes only disc.json.

## Working agreement (Andy, today)

- Andy said the learning goal of the port was lost: Claude writes, Andy directs and tests. He will
  not learn on this feature; he intends to learn when Remux/Transcode are restructured properly
  (Step 12: UserControls, DataGrid, settings as data). In that work Claude explains and coaches
  rather than handing over finished files.
- Claude claimed the codebase had no DataGrid; Andy believed it did. Verified on all branches:
  only the WinForms `DataGridView` comments and the Step 12 backlog line mention it.

## Files changed since `0f3d208` (all three zips)

New: `MonitorWorkArea.cs`, `Ripping\TitleNaming.cs`.
Changed: `AppSettings.cs`, `MainWindow.xaml.cs`, `UserPreferences.xaml(.cs)`,
`Themes\ThemeResources.xaml`, `Ripping\DiscRecord.cs`, `Ripping\RipView.xaml(.cs)`,
`docs\TranscodeTools_Reference.md`.
