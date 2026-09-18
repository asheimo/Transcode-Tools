# Transcode Tools WPF — Project Reference & Scope

## Overview

Transcode Tools is a personal media processing utility migrated from VB.NET WinForms to C# WPF (.NET 9). The application uses mkvmerge, ffmpeg, ffprobe, and robocopy to remux and transcode MKV video files. The core goal is maximum file size reduction without unacceptable compromise to video or audio quality, leveraging NVIDIA GPU hardware acceleration throughout the processing pipeline.

## Migration Status

| Step | Status | Notes |
| --- | --- | --- |
| Step 1 — Basic layout | **✓ Complete** | |
| Step 2 — User Preferences | **✓ Complete** | other-transcode and Ruby removed |
| Step 3 — File browser / TreeView | **✓ Complete** | |
| Step 4 — ffprobe integration | **✓ Complete** | |
| Step 5 — Settings save/load | **✓ Complete** | CommandBuilder.cs |
| Step 6a — Run window: Remux | **✓ Complete** | |
| Step 6b — Run window: Transcode | **✓ Complete** | Direct ffmpeg CUDA pipeline; confirmed optimal settings `-preset p5 -cq 19 -spatial-aq 1 -aq-strength 10` |
| Step 7 — Style MessageBoxes | **✓ Complete** | Aero2 ControlTemplate override |
| Step 8 — TV show folder hierarchy | **✓ Complete** | Show/Season TreeView nodes |
| Step 9 — Right-click rename from TreeView | **✓ Complete** | File rename + .txt settings rename; case-only fix with two-step temp rename |
| Step 10 — Adjustable splitter | **✓ Complete** | Three-dot grip, shared column layout |
| Step 11 — App opens centered | **✓ Complete** | `WindowStartupLocation=CenterScreen` |
| Step 12 — Code refactor/cleanup | **Pending** | Includes dark mode/Fluent theme, DataGrid conversion |

### UI Enhancements

- Recently loaded folders: history dropdowns for input and output, persisted, configurable size in User Preferences
- Adjustable splitter with three-dot grip handle between folder list and file tree
- App opens centered on screen
- ComboBox full Aero2 ControlTemplate override with rounded corners
- TreeViewItem full ControlTemplate override — eliminates Aero2 RelativeSource binding errors
- Default window width: 1000px, 50/50 panel split
- Run window: episode-level checkboxes show white (no settings) or green (has settings), matching movie-level behaviour
- Run window: full command displayed before each file processes (with blank line separator). Transcode no-settings path runs from defaults without saving a .txt file.
- Log files: written to `<OutputDir>\Logs\yyyyMMdd_HHmmss\<FolderName>\<n>.log`. Contains timestamped header, command, and run output. Progress lines stripped from plain logs. View Log right-click opens session folder in Explorer (hidden when logging off). Logs folder excluded from folder tree.
- Verbose logging — split into two independently toggled settings under Write Log Files. mkvmerge Verbose Logging (Remux): invokes mkvmerge with `-v`, raising verbosity to level 2 and adding Matroska element detail to the log file. ffmpeg Verbose Logging (Transcode): ffmpeg runs at user-selected log level (warning/info/verbose/debug, default verbose) without `-stats`; all output redirected to log file; Run window shows animated text ticker "Processing… [---- ] Details in log file". Ticker clears on completion. Log level resets to verbose when ffmpeg verbose logging is disabled. Both settings are gated by Write Log Files and are injected into the command at runtime — the saved settings .txt file stays canonical.
- Right-click rename from folder list (folder-level rename) — completed
- Audio drag disabled state: no cursor feedback when fewer than 2 tracks selected — completed
- User Preferences: Delete settings .txt from within app (with confirmation) — completed
- User Preferences: Browse/open-in-explorer for settings .txt folder — completed
- User Preferences: NVENC Quality Flags field added to Default Options — completed
- User Preferences: Default NVENC Preset dropdown (None, p1–p7) added to Default Options — completed
- Auto-move completed folders — on a successful run, source folders are moved to `<InputDirectory>\Completed\` via `Directory.Move`. Moves run in parallel with processing; each folder is queued immediately when all its files succeed. TreeViewItem background flashes amber then green on success, red on failure. TV show rows colour at the show level. Cancel waits for in-flight moves to finish before enabling Close. Opt-out checkbox in User Preferences ("Disable automatic move of completed folders") defaults off. MainWindow tree refreshes on Run window close.
- Run window output checkmarks — on open and after each run, an async background scan compares input folder .mkv files against the output folder. Appends an amber ✓ to folder labels where all files are present, and to individual file labels in Show Files mode. Label text colour is unchanged. Cancels and restarts on Show Files toggle.
- Subtitle track naming — Title column added to the Remux subtitle list. Display-only cell; edited via right-click → "Edit Title…" which opens a modal dialog. Probed from `tags.title` on load. Emitted as `--track-name TID:"value"` to mkvmerge when non-empty. Restored from the saved settings .txt file via regex on the command string. Carries through any subsequent transcode automatically because `-c:s copy` preserves the Matroska Name element mkvmerge writes. Scope is subtitles only — audio Title remains display-only for now.
- View Command — always builds from current UI state; blocking conditions (no output directory, no audio selected) fire normally. If a settings file exists, compares current command against saved file; if different, shows amber warning: "The current selections differ from the saved settings file."
- Settings mismatch detection — on settings file load: Preset field turns amber when saved preset differs from `AppSettings.DefaultPreset`. Warning column appears in video table when saved NVENC quality flags differ from `AppSettings.NvencQualityFlags` (compared as `-flag value` pairs). Both exempt for DoVi streams. Column removed automatically when switching to a file with no mismatch.
- Screen-sleep / system-sleep keep-awake — while a Run window is processing, the app calls `SetThreadExecutionState` with `ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED` to prevent Windows idle-timer sleep and keep the display on. Released on run completion, cancel, or window close. Fixes the NVENC stall observed when the display sleeps mid-job (NVIDIA driver drops the GPU to a low-power state and the encode session stalls until the display wakes), and the latent risk of full system sleep mid-job. Implemented in `PowerKeepAwake.cs`; acquired/released around the run loop in `RunRemux.StartBtn_Click`. Applies to both remux and transcode runs.
- Single-instance enforcement — `App.OnStartup` acquires a named mutex (`Local\TranscodeTools_SingleInstance_v1`) before any other initialisation. A second launch detects the existing instance, brings its main window to the foreground via `SetForegroundWindow` (with `ShowWindow` restore if minimised), shows an "Already Running" message box, and exits cleanly without constructing MainWindow. Mutex released in `OnExit`. `Local\` scope means concurrent instances are blocked per Windows user session — different users on the same machine each get their own. Single Run window per app instance is already enforced by `MainWindow.Run_Click` using `ShowDialog` (modal); no additional guard needed.

## File Name Corrections

All three features are grouped under 'File Name Corrections' in User Preferences. Load-time order: title case first, resolution append second. Title case and resolution append are suppressed in Transcode mode — folder names are assumed correct on the transcode side. Missing year detection runs in both modes.

### Title Case Correction

- Summary dialog on folder load shows proposed renames with per-item checkboxes
- Permanent dialog — user reviews and selectively applies on every load
- Renames folder on disk + matching .txt settings files
- User-editable acronym list in preferences (HDR, UHD, DTS, etc.)
- Preference checkbox to disable (default: on)
- Case-only renames use two-step temp folder as intermediate step (Windows case-insensitive FS)
- Extras/featurette .mkv files auto-renamed on folder select (no dialog) — applies `ToTitleCase` to files with a known category suffix (`-featurette`, `-deleted`, `-behindthescenes`, etc.) in both movie and TV season folders. Skips main title files and S##E## episode files. Matching .txt settings files renamed automatically.

### Auto-Append Resolution

- Probes main title files with ffprobe on folder load (movies only, not TV)
- Appends resolution label (`-1080p`, `-2160p` etc.) to filenames that lack one
- Skips files with existing resolution suffix by default
- 'Always verify via ffprobe' option in preferences (default: off)
- Renames matching .txt settings files
- Per Plex docs: resolution label comes after edition tag — e.g. `Title (Year) {edition-X}-1080p.mkv`

### Missing Year Detection

- Amber ⚠ warning on FolderNode (left panel) when folder name has no (YYYY)
- Amber ⚠ warning on FileLeafNode (right panel) for main title files only
- Tooltip: 'Missing Year Label'
- Visual flag only — no auto-correction
- TV episode files under SeasonNode are exempt from the warning

## Video Pipeline

### Hardware Acceleration

- `-hwaccel cuda -hwaccel_output_format cuda`
- `-c:v <codec>_cuvid` (hardware decode — codec from ffprobe: h264_cuvid, hevc_cuvid, mpeg2_cuvid)
- `-filter:v scale_cuda=format=yuv420p` (non-burn path uses p010le; burn path uses yuv420p — overlay_cuda requires yuv420p input)
- `-c:v hevc_nvenc` (hardware encode — primary target)
- `-profile:v main10` (replaces `-highbitdepth`; faster at 286fps vs 278fps on h264 1080p; works for both burn and non-burn paths)

*Note: `-colorspace:v bt709` is omitted when encoding HEVC — it breaks the CUDA pipeline.*

### Output Codec — OPEN ITEMS

- Default: HEVC (H.265) Main 10 via hevc_nvenc
- Optional: H.264 via h264_nvenc (secondary)

### Confirmed Optimal Encode Settings — Nvidia (Pixel-Level Tested)

- `-preset p5`
- `-cq 19` (constant quality; cq16/17 indistinguishable from cq19)
- `-spatial-aq 1` (spatial adaptive quantisation)
- `-aq-strength 10` (sweet spot for detail retention)
- `-profile:v main10` (encoder handles 10-bit promotion; replaces `-highbitdepth`)

*~36% sharpness loss vs source is the RTX 3060 NVENC hardware ceiling. No flag combination improves it further at acceptable FPS.*

### HDR Passthrough (4K) — OPEN ITEMS

- HDR10: static metadata passthrough via `-master_display`, `-max_cll`, `-color_primaries`, `-color_trc`, `-colorspace`
- HDR10+: dynamic metadata — shelved. Detected at probe time and flagged with solid blue cell highlight (#3498DB) in the Track Info column so files with HDR10+ data can be identified when implementation is revisited.
- Dolby Vision: NVENC cannot preserve RPU — stream copy only. Detected via `side_data_list` DOVI configuration record. OutputFormat locked to copy (DoVi), hwaccel/nvenc bypassed. Track Info cell highlighted amber (#FFE08A) with black text.

### Deinterlace / Telecine

- ffprobe reads `field_order`; sets `IsInterlaced` on TranscodeVideoTrack for tt/bb/tb/bt values
- CommandBuilder adds `yadif_cuda=mode=1` to filter chain when `IsInterlaced` is true; correctly chains with `scale_cuda=format=p010le` when both needed: `yadif_cuda=mode=1,scale_cuda=format=p010le`
- Track Info cell highlighted purple (#9B59B6) when interlaced; amber (#FFE08A) for DoVi; blue (#3498DB) for HDR10+. Cell-only highlight — does not affect dropdown controls.
- Notice column added dynamically when any interlaced track is detected; removed automatically when switching to a non-interlaced file
- Tooltip on Track Info cell shows full text when truncated

## Audio Pipeline

### Per-Track Decision Model — COMPLETED

- AAC → Copy
- AC3 → Copy
- EAC3 → Copy or re-encode at different bitrate (user choice)
- DTS-HD / TrueHD / Atmos → Encode to EAC3/AC3/AAC at user-selected bitrate
- PCM / FLAC → Encode to EAC3/AC3/AAC at user-selected bitrate

### NOTE: Lossless as Encode Source

A lossless track must be used as the source when encoding to lossy, even when an existing lossy track is present. Lossy-to-lossy re-encoding compounds quality loss and must be avoided. When both a lossless copy and a derived lossy track are wanted in the output, use the + expander sub-row on the lossless track. Parent and sub-rows act independently — unchecking the parent does not affect sub-rows, allowing the lossless to be excluded while keeping only the derived lossy track.

### Audio Track Controls

- Phase 1 — `IsSelected` checkbox per audio track (default true, opposite of remux). Lossless tracks (TrueHD, DTS-HD MA, FLAC, PCM) get + expander for derived lossy sub-rows. Multiple sub-rows allowed per parent. Parent row Format and BitRate locked to Keep (copy only) when sub-rows exist. Parent and sub-rows act independently. Sub-rows default to eac3/640k. Output order: lossless first, then derived lossy in order added. Validation blocks save/view when no tracks selected.
- Phase 2 — Width dropdown enabled (Keep, 5.1, Stereo). Stereo auto-applies `-af aresample=matrix_encoding=dplii`. Width options capped at source channel count — no upmixing. BitRate dropdown dynamic per Width: Stereo capped at 640, 5.1 and above up to 1536. Bitrate ceiling enforced on lossy sources. 768 removed as non-standard. Cascade resets: Width change resets BitRate if now out of range; Format change to Keep resets both.
- BitRate options: 1536, 1024, 640, 448, 384, 320, 256, 192. (768 removed — non-standard)
- Archiving strategy: keep remuxed version as archive master (not raw rip, not transcode). Preferred output audio: lossless source encoded to eac3 for maximum Plex compatibility. DTS vs eac3 distinction is largely folklore at equivalent bitrates — eac3 wins on device compatibility.
- Sub-row restore on settings load. Saved commands with duplicate `-map 0:a:N` entries are detected on load and derived lossy sub-rows are synthesised automatically.

## Subtitles

- Default: copy selected subtitle tracks
- Burn checkbox restore on settings load implemented — the saved command is searched via regex for the bracketed `[0:s:N]` token inside filter_complex (token-scanning on `command.Split(' ')` does not work because the quoted filter_complex value collapses into a single token)
- Green bar artifact at bottom of frame resolved via hevc_metadata bitstream filter (see SPS crop fix below)
- Cancel warning message now distinguishes auto-move state: reads "Job cancelled." when auto-move is disabled, and "Job cancelled. Waiting for folder moves to complete…" otherwise
- PGS/VOBSUB confirmed working filter chain (GPU overlay): `[0:v]scale_cuda=format=yuv420p[main];[0:s:0]fps=24000/1001,format=yuva420p,hwupload_cuda[sub];[main][sub]overlay_cuda=ts_sync_mode=nearest:repeatlast=1[vout]`
- The `fps=` filter on the subtitle branch is critical — sub2video emits at its own frame rate; matching fps to the main video corrects ~1.5s caption timing lag
- overlay_cuda does not support p010le input; `-profile:v main10` on the encoder handles 10-bit promotion instead
- NPP not required; scale_npp deprecated. CommandBuilder.cs updated to confirmed chain; outSubIdx fix in place
- SPS crop fix: overlay_cuda does not propagate the main input's display crop to its output. For sources whose height is not 16-aligned (e.g. 1080), NVDEC decodes into a 16-aligned CUDA surface (1088) and the overlay produces a 1088-tall frame with uninitialized memory in the bottom N rows, which renders green in YUV
- Fix: append `-bsf:v hevc_metadata=crop_bottom=N` on the NVENC HEVC burn path where N = (16 - height % 16) % 16. Metadata-only, no re-encode, no performance impact. Produces a file with display_height=1080, coded_height=1088, matching the non-burn path and every legitimate 1080p HEVC file
- Gated on `!useIntel && useHevc && hasBurn && vPad > 0` — QSV handles crop correctly and 16-aligned heights (720p, 4K) need no adjustment
- `-profile main10` ambiguity warning from ffmpeg — use `-profile:v main10` (fix pending in CommandBuilder.cs)
- Text-based (SRT/ASS): rasterized via libass on CPU; same GPU composite path applies

## Intel GPU / QSV Support — OPEN ITEMS

- CommandBuilder branches on `AppSettings.GpuVendor` ("NVIDIA" or "Intel")
- Intel QSV pipeline: `-hwaccel qsv -hwaccel_output_format qsv`
- Hardware decoders: `_qsv` variants (h264_qsv, hevc_qsv, mpeg2_qsv, etc.)
- Hardware encoders: hevc_qsv / h264_qsv
- `-highbitdepth` flag is NVENC-only and is omitted from the QSV path
- For 8-bit→10-bit conversion and deinterlace, vpp_qsv is used (not scale_qsv — see QSV filter notes below)
- Default QSV quality flags: `-global_quality 23 -scenario 3 -mbbrc 1 -rdo 1 -adaptive_i 1 -adaptive_b 1`
- All confirmed valid for both hevc_qsv and h264_qsv
- `-look_ahead` is h264_qsv only
- `-look_ahead_depth` requires `-extbrc 1` for hevc_qsv
- `-global_quality` activates ICQ mode and requires `-preset` to be set
- QSV 8-bit→10-bit conversion and deinterlace use vpp_qsv (the oneVPL Video Processing Pipeline filter), not scale_qsv
- scale_qsv has no range control and expands limited-range (16–235) pixel values to full range during yuv420p→p010le conversion, washing out blacks
- vpp_qsv provides documented `out_range=tv` support which preserves limited-range values correctly
- Filter used: `-filter:v vpp_qsv=format=p010le:out_range=tv:scale_mode=hq`
- `scale_mode=hq` is required — the default auto mode resolves to low_power, producing visibly softer output
- Deinterlace is combined in the same filter pass via `deinterlace=2` when the source is interlaced
- Pixel-level analysis confirmed 1.14/255 average difference in dark areas vs source, with true black preserved — the remaining visual difference when comparing in mpv is a player rendering artifact between MPEG-2 and HEVC color paths, not an encode problem

## Tabbed Preferences

- User Preferences window redesigned with five tabs: Tool Paths, Remux, Encode, File Names, Logging
- Encode tab shows vendor-specific panels (PnlNvidia / PnlIntel) that swap dynamically when GPU Vendor is changed; each vendor has its own preset combo and quality flags field
- Logging tab groups verbose logging settings into two sub-sections under Write Log Files — Remux (mkvmerge Verbose Logging) and Transcode (ffmpeg Verbose Logging + ffmpeg Log Level dropdown) — each gated by the master Write Log Files toggle
- ThemeResources.xaml adds full TabControl and TabItem ControlTemplate overrides matching the flat Aero2 theme

## Test Mode

- Test Mode checkbox in the Run Transcode toolbar
- Only enabled in transcode mode when "Show individual files" is checked and exactly one file is selected
- Encodes 5 minutes of output starting at the 10-minute mark using output-side seek (`-ss 00:10:00 -t 00:05:00` placed after the input file path)
- Output file is renamed with a `[test-qsv]` or `[test-nvenc]` moniker (e.g. `Beetlejuice (1998)-480p [test-qsv].mkv`) and written to the normal output folder
- If the user checks a second file while test mode is on, a warning is shown and test mode is cleared

## Active Backlog

- HDR10+ dynamic metadata passthrough — shelved pending implementation; visual flag (blue cell #3498DB) implemented at probe time for identification
- Post-transcode ffprobe verification — compare duration, resolution, audio track count against source; log pass/fail. Log file infrastructure now in place.
- Auto-move testing — pending: TV show colouring (multi-season row colours up); partial TV show failure (show row stays default); cancel mid-run (waiting message, completed folders still move); cancel before any folder completes (no moves, straight to Close); same folder run twice (skipped, colours red); `Completed\`/`Logs\`/`Remux\`/`Transcode\` hidden in both trees.
- User wiki / manual — user-facing documentation covering all features; to be written once feature set is stable
- robocopy verbose logging — deferred pending assessment of practical value (mkvmerge verbose logging implemented)
- IsDefault/IsForced checkbox rework (currently read-only)
- Debug/comparison tool: time definition input for frame captures at same timestamp for source vs transcode
- CPU encoding (x264/x265) — rough implementation next, followed by design discussion on how to handle encode method selection (NVENC vs QSV vs CPU) in the UI
- Resolution dropdown (pending ffmpeg scaling)
- Fallback hardware decoder revisit
- Code refactor/cleanup (Step 12)
- Dark mode / Fluent polish (Step 12)
- DataGrid conversion from ListView+GridView (Step 12)

## Development Rules

Rules are maintained in a separate document: [Development_Rules.md](Development_Rules.md). Read that document at the start of every session.
