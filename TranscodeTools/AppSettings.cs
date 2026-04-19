// ============================================================
// AppSettings.cs
// ------------------------------------------------------------
// This file defines a SETTINGS class for storing user
// preferences between sessions.
//
// In VB.NET you used My.Settings which was auto-generated.
// In C# WPF we use Properties.Settings in a similar way,
// but we need to define the settings class manually.
//
// We use a simple JSON file approach here which is more
// modern and portable than the old .settings XML format.
// The settings file is saved in the user's AppData folder.
// ============================================================

using System.IO;
using System.Text.Json;          // Built-in JSON support in .NET 8
using System.Text.Json.Serialization;

namespace TranscodeTools;

// This is a plain class that holds all our settings as properties.
// "sealed" means no other class can inherit from it — a good
// practice for utility/singleton classes.
public sealed class AppSettings
{
    // ── Singleton pattern ────────────────────────────────────────────
    // A singleton ensures only ONE instance of this class ever exists.
    // Any code that needs settings calls AppSettings.Instance
    // rather than creating a new AppSettings() each time.
    //
    // "static" means the property belongs to the CLASS itself,
    // not to any particular instance — same as Shared in VB.NET.
    private static AppSettings? _instance;
    public static AppSettings Instance => _instance ??= Load();
    // ^ ??= is the "null-coalescing assignment" operator.
    //   It means: if _instance is null, call Load() and assign
    //   the result to _instance, then return it.
    //   Equivalent to: If _instance Is Nothing Then _instance = Load()

    // ── File path ────────────────────────────────────────────────────
    // Where we store the settings JSON file on disk.
    // Environment.GetFolderPath gets the user's AppData\Roaming folder.
    // Path.Combine safely joins path segments with the right separator.
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TranscodeTools",
        "settings.json");

    // ── Tool path settings ───────────────────────────────────────────
    // Each property maps directly to a My.Settings entry from the
    // original VB.NET project. Default value is empty string "".
    //
    // The { get; set; } syntax is an AUTO-PROPERTY — C# generates
    // the backing field automatically. Equivalent to:
    //   Public Property MPV_Path As String = ""  in VB.NET
    public string MPV_Path             { get; set; } = "";
    public string SubtitleEdit_Path    { get; set; } = "";
    public string FFmpeg_Path          { get; set; } = "";
    public string FFprobe_Path         { get; set; } = "";
    public string MKVPropEdit_Path     { get; set; } = "";
    public string MKVMerge_Path        { get; set; } = "";

    // ── Default/options strings ──────────────────────────────────────
    // These match the original My.Settings defaults exactly.
    public string MKVMerge_Defaults       { get; set; } = "";
    public string MKVMerge_Options        { get; set; } = "--no-buttons --no-attachments";
    public string RoboCopy_Defaults       { get; set; } = "/is /njs /ndl /nc /ns";

    // ── Transcode behaviour ──────────────────────────────────────────
    // When true, files without a settings file in Transcode mode are
    // automatically built from defaults and run. When false, they are
    // robocopied, requiring the user to explicitly save settings first.
    public bool AlwaysConvertToHevc { get; set; } = true;

    // ── GPU vendor selection ─────────────────────────────────────────
    // Controls which hardware acceleration pipeline CommandBuilder emits.
    // "NVIDIA" → CUDA/NVENC/CUVID (existing pipeline).
    // "Intel"  → QSV pipeline (Intel Quick Sync Video).
    // Changing this does not invalidate saved settings files — the saved
    // command is stored verbatim and is not rebuilt from the vendor setting.
    // Only new builds (View Command / Save Settings / run without settings)
    // pick up the active vendor.
    public string GpuVendor { get; set; } = "NVIDIA";

    // ── NVENC defaults ───────────────────────────────────────────────
    // DefaultPreset: the preset applied to new files on load. "None" omits
    // the -preset flag entirely, letting NVENC choose automatically.
    // NvencQualityFlags: the quality flag string appended after -preset.
    // Defaults match the confirmed optimal pixel-tested settings.
    public string DefaultPreset      { get; set; } = "p5";
    public string NvencQualityFlags  { get; set; } = "-cq 19 -spatial-aq 1 -aq-strength 10";

    // ── QSV defaults ─────────────────────────────────────────────────
    // QsvQualityFlags: quality flag string appended after -preset for Intel QSV.
    // All flags below are confirmed valid for both hevc_qsv and h264_qsv.
    //
    // -global_quality 23: ICQ (intelligent constant quality) mode — the QSV
    //   equivalent of NVENC -cq. Lower = better quality. 23 is a good starting
    //   point; adjust down (e.g. 20) for higher quality at larger file sizes.
    //   Requires -preset to be set (not None) to activate ICQ mode.
    // -scenario 3: hints the encoder this is archival content, biasing toward
    //   quality over latency. No performance cost.
    // -mbbrc 1: macroblock-level bitrate control — varies quantization per
    //   macroblock rather than per frame. Equivalent in spirit to NVENC -spatial-aq.
    // -rdo 1: rate distortion optimisation — better quantization decisions per block.
    // -adaptive_i 1: adaptive I-frame placement — better for scene cuts.
    // -adaptive_b 1: adaptive B-frame placement — better for motion content.
    //
    // Note: -look_ahead is h264_qsv only. hevc_qsv uses -look_ahead_depth with
    //   -extbrc 1 instead. Not included by default; add manually if needed.
    // Note: -global_quality only applies when -preset is also set (QSV ICQ
    //   mode requires a preset). DefaultPreset should be "medium" for QSV.
    public string QsvQualityFlags { get; set; } = "-global_quality 23 -scenario 3 -mbbrc 1 -rdo 1 -adaptive_i 1 -adaptive_b 1";

    // ── Sub-row default bit rate ─────────────────────────────────────
    // Applied when a new derived lossy sub-row is created via the + button
    // or restored from a settings file that has no explicit bitrate saved.
    public string SubRowDefaultBitRate { get; set; } = "640";

    // ── File Name Corrections ───────────────────────────────────────
    // Title Case: auto-corrects display names on folder load.
    // Acronym list prevents known uppercase terms being mangled by ToTitleCase.
    public bool TitleCaseEnabled { get; set; } = true;
    public List<string> TitleCaseAcronyms { get; set; } = new()
    {
        "HDR", "HDR10", "HDR10+", "UHD", "SDR", "SDH",
        "DTS", "AC3", "EAC3", "AAC", "FLAC", "TrueHD", "Atmos",
        "HEVC", "AVC", "VC1", "H264", "H265",
        "REMUX", "IMAX", "3D", "4K"
    };

    // Resolution Append: probes main title files with ffprobe and appends
    // resolution label (e.g. -1080p, -2160p) to filenames that lack one.
    // ResolutionVerifyAlways: when true, probes even files that appear to
    // already have a resolution suffix. Affects performance — default off.
    public bool ResolutionAppendEnabled  { get; set; } = true;
    public bool ResolutionVerifyAlways   { get; set; } = false;

    // ── Run / Log settings ───────────────────────────────────────────
    // WriteLogFiles: when true, a log file is written per processed file
    // to <OutputDirectory>\Logs\<timestamp>\<FolderPath>\<Name>.log.
    // VerboseLogging: only active when WriteLogFiles is on. When true,
    // ffmpeg runs at FfmpegLogLevel (without -stats) and all output goes
    // to the log file; the Run window shows an indeterminate progress bar.
    // When false, ffmpeg runs with -loglevel error -stats as normal and
    // the log file captures the same output shown in the window.
    // FfmpegLogLevel: the -loglevel value used when VerboseLogging is on.
    public bool   WriteLogFiles   { get; set; } = false;
    public bool   VerboseLogging  { get; set; } = false;
    public string FfmpegLogLevel  { get; set; } = "verbose";

    // ── Auto-move completed folders ──────────────────────────────────
    // When false (default), each output folder whose files all completed
    // without errors is moved to <OutputDirectory>\Completed\<FolderName>
    // at the end of a run. Set to true to disable this behaviour.
    // Uses Directory.Move — fast but requires source and destination to be
    // on the same drive.
    public bool DisableMoveCompleted { get; set; } = false;

    // ── Recent folder history ────────────────────────────────────────
    // Stores the most recently used input and output folder paths so the
    // user can quickly reload them from a dropdown without browsing again.
    // Both lists share a single max size setting.
    public List<string> RecentInputFolders  { get; set; } = new();
    public List<string> RecentOutputFolders { get; set; } = new();
    public int RecentFolderHistorySize      { get; set; } = 6;

    // Prepends a path to the recent input/output list, deduplicates, and
    // trims to RecentFolderHistorySize. Call after a successful selection.
    public void AddRecentInput(string folder)  => AddRecent(RecentInputFolders,  folder);
    public void AddRecentOutput(string folder) => AddRecent(RecentOutputFolders, folder);

    private void AddRecent(List<string> list, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        // Remove any existing entry (case-insensitive) so it does not appear
        // twice, then insert at the top as the most recently used.
        list.RemoveAll(p => p.Equals(folder, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, folder);
        // Trim to the configured max size
        while (list.Count > RecentFolderHistorySize)
            list.RemoveAt(list.Count - 1);
    }

    // Load() reads the JSON file from disk and deserialises it into
    // an AppSettings object. If the file doesn't exist yet (first run)
    // it returns a new AppSettings with all the default values above.
    // "static" — called on the class, not an instance.
    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                // JsonSerializer.Deserialize converts JSON text back into
                // a C# object. The ?? at the end means "if result is null,
                // use a new AppSettings() instead".
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* If anything goes wrong, fall through to defaults */ }

        return new AppSettings();
    }

    // Save() serialises the current settings to JSON and writes to disk.
    // "this" refers to the current instance — same as "Me" in VB.NET.
    public void Save()
    {
        try
        {
            // Make sure the folder exists before trying to write the file.
            // CreateDirectory does nothing if the folder already exists.
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);

            // JsonSerializerOptions controls how the JSON is formatted.
            // WriteIndented = true makes it human-readable (pretty printed).
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not save settings:\n{ex.Message}",
                "Settings Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    // Helper to check if all required tool paths have been set.
    // Returns true only if every mandatory path has a value.
    // Used on startup and when the user clicks OK in UserPreferences.
    public bool AllPathsSet() =>
        !string.IsNullOrWhiteSpace(MPV_Path)          &&
        !string.IsNullOrWhiteSpace(SubtitleEdit_Path) &&
        !string.IsNullOrWhiteSpace(FFmpeg_Path)       &&
        !string.IsNullOrWhiteSpace(FFprobe_Path)      &&
        !string.IsNullOrWhiteSpace(MKVPropEdit_Path)  &&
        !string.IsNullOrWhiteSpace(MKVMerge_Path);
}
