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

    // ── NVENC defaults ───────────────────────────────────────────────
    // DefaultPreset: the preset applied to new files on load. "None" omits
    // the -preset flag entirely, letting NVENC choose automatically.
    // NvencQualityFlags: the quality flag string appended after -preset.
    // Defaults match the confirmed optimal pixel-tested settings.
    public string DefaultPreset      { get; set; } = "p5";
    public string NvencQualityFlags  { get; set; } = "-cq 19 -spatial-aq 1 -aq-strength 10";

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
