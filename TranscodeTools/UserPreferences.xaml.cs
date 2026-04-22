// ============================================================
// UserPreferences.xaml.cs
// ------------------------------------------------------------
// Code-behind for the User Preferences window.
// Handles loading settings into the form, saving them back,
// and the Where/Browse button logic for each tool path.
//
// Encode tab notes:
//   CbxGpuVendor selection shows/hides PnlNvidia or PnlIntel.
//   Each panel has its own preset ComboBox:
//     CbxDefaultPreset    — NVIDIA (p1–p7 / None)
//     CbxQsvDefaultPreset — Intel  (veryfast–veryslow)
//   Only the active panel's combo is read/written at save time.
//   AppSettings.DefaultPreset stores a single string; we just
//   save whichever vendor is currently selected.
// ============================================================

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TranscodeTools;

public partial class UserPreferences : Window
{
    // ── Snapshot for Cancel ──────────────────────────────────────────
    // Immutable copy of all settings captured when the window opens.
    // Cancel restores from this rather than from disk, so in-memory
    // state is always consistent with what the user actually saved.
    private record SettingsSnapshot(
        string MPV_Path,
        string SubtitleEdit_Path,
        string FFmpeg_Path,
        string FFprobe_Path,
        string MKVPropEdit_Path,
        string MKVMerge_Path,
        string MKVMerge_Defaults,
        string MKVMerge_Options,
        string RoboCopy_Defaults,
        bool   AlwaysConvertToHevc,
        string GpuVendor,
        string DefaultPreset,
        string NvencQualityFlags,
        string QsvQualityFlags,
        string SubRowDefaultBitRate,
        int    RecentFolderHistorySize,
        string TitleCaseAcronyms,
        bool   TitleCaseEnabled,
        bool   ResolutionAppendEnabled,
        bool   ResolutionVerifyAlways,
        bool   AutoCorrectResolutionMismatch,
        bool   WriteLogFiles,
        bool   MkvMergeVerbose,
        bool   FfmpegVerboseLogging,
        string FfmpegLogLevel,
        bool   DisableMoveCompleted
    );

    private readonly SettingsSnapshot _snapshot;

    // ── Constructor ──────────────────────────────────────────────────
    public UserPreferences()
    {
        InitializeComponent();

        // Capture snapshot BEFORE LoadSettings() — snapshot must reflect
        // the on-disk state, not any default the UI might substitute.
        var s = AppSettings.Instance;
        _snapshot = new SettingsSnapshot(
            s.MPV_Path,
            s.SubtitleEdit_Path,
            s.FFmpeg_Path,
            s.FFprobe_Path,
            s.MKVPropEdit_Path,
            s.MKVMerge_Path,
            s.MKVMerge_Defaults,
            s.MKVMerge_Options,
            s.RoboCopy_Defaults,
            s.AlwaysConvertToHevc,
            s.GpuVendor,
            s.DefaultPreset,
            s.NvencQualityFlags,
            s.QsvQualityFlags,
            s.SubRowDefaultBitRate,
            s.RecentFolderHistorySize,
            string.Join(",", s.TitleCaseAcronyms),
            s.TitleCaseEnabled,
            s.ResolutionAppendEnabled,
            s.ResolutionVerifyAlways,
            s.AutoCorrectResolutionMismatch,
            s.WriteLogFiles,
            s.MkvMergeVerbose,
            s.FfmpegVerboseLogging,
            s.FfmpegLogLevel,
            s.DisableMoveCompleted
        );

        LoadSettings();
    }

    // ── Click-to-select-all ──────────────────────────────────────────
    // Fires on every keyboard-focus change within the window.
    // If a TextBox gained focus, select its entire contents.
    // One handler on the Window covers all text boxes automatically.
    private void Window_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is TextBox tb)
            tb.SelectAll();
    }

    // ── LoadSettings ─────────────────────────────────────────────────
    // Reads AppSettings.Instance and populates every control.
    private void LoadSettings()
    {
        var s = AppSettings.Instance;

        // Tool Paths tab
        TbxMPV.Text            = s.MPV_Path;
        TbxSubtitleEdit.Text   = s.SubtitleEdit_Path;
        TbxFFmpeg.Text         = s.FFmpeg_Path;
        TbxFFprobe.Text        = s.FFprobe_Path;
        TbxMKVPropEdit.Text    = s.MKVPropEdit_Path;
        TbxMKVMerge.Text       = s.MKVMerge_Path;

        // Encode tab — vendor selector (triggers panel swap via SelectionChanged)
        CbxGpuVendor.SelectedItem = s.GpuVendor;
        if (CbxGpuVendor.SelectedItem == null) CbxGpuVendor.SelectedIndex = 0;

        // Load the saved preset into the correct vendor combo.
        // The other combo keeps its XAML default — the user only interacts
        // with one at a time, and SaveSettings only reads the active one.
        var isIntel = s.GpuVendor.Equals("Intel", StringComparison.OrdinalIgnoreCase);
        if (isIntel)
        {
            CbxQsvDefaultPreset.SelectedItem = s.DefaultPreset;
            if (CbxQsvDefaultPreset.SelectedItem == null) CbxQsvDefaultPreset.SelectedIndex = 3; // medium
        }
        else
        {
            CbxDefaultPreset.SelectedItem = s.DefaultPreset;
            if (CbxDefaultPreset.SelectedItem == null) CbxDefaultPreset.SelectedIndex = 5; // p5
        }

        TbxNvencQualityFlags.Text      = s.NvencQualityFlags;
        TbxQsvQualityFlags.Text        = s.QsvQualityFlags;
        CbxSubRowDefaultBitRate.SelectedItem = s.SubRowDefaultBitRate;
        if (CbxSubRowDefaultBitRate.SelectedItem == null) CbxSubRowDefaultBitRate.SelectedIndex = 2; // 640
        ChkAlwaysConvertToHevc.IsChecked = s.AlwaysConvertToHevc;

        // Remux tab
        TbxMKVMergeDefaults.Text = s.MKVMerge_Defaults;
        TbxMKVMergeOptions.Text  = s.MKVMerge_Options;
        TbxRoboCopyDefaults.Text = s.RoboCopy_Defaults;

        // File Names tab
        ChkTitleCase.IsChecked              = s.TitleCaseEnabled;
        TbxAcronyms.Text                    = string.Join(", ", s.TitleCaseAcronyms);
        ChkResolutionAppend.IsChecked              = s.ResolutionAppendEnabled;
        ChkResolutionVerifyAlways.IsChecked        = s.ResolutionVerifyAlways;
        ChkAutoCorrectResolutionMismatch.IsChecked = s.AutoCorrectResolutionMismatch;
        ChkAutoCorrectResolutionMismatch.IsEnabled = s.ResolutionVerifyAlways;

        // Run tab
        ChkWriteLogFiles.IsChecked          = s.WriteLogFiles;
        ChkMkvMergeVerbose.IsChecked        = s.MkvMergeVerbose;
        ChkMkvMergeVerbose.IsEnabled        = s.WriteLogFiles;
        ChkFfmpegVerboseLogging.IsChecked   = s.FfmpegVerboseLogging;
        ChkFfmpegVerboseLogging.IsEnabled   = s.WriteLogFiles;
        CbxFfmpegLogLevel.SelectedItem      = s.FfmpegLogLevel;
        if (CbxFfmpegLogLevel.SelectedItem == null) CbxFfmpegLogLevel.SelectedIndex = 2; // verbose
        CbxFfmpegLogLevel.IsEnabled         = s.WriteLogFiles && s.FfmpegVerboseLogging;
        ChkAutoMoveCompleted.IsChecked      = s.DisableMoveCompleted;
        TbxHistorySize.Text                 = s.RecentFolderHistorySize.ToString();
    }

    // ── SaveSettings ─────────────────────────────────────────────────
    // Writes all control values back to AppSettings and persists to disk.
    private void SaveSettings()
    {
        var s = AppSettings.Instance;

        // Tool Paths
        s.MPV_Path          = TbxMPV.Text;
        s.SubtitleEdit_Path = TbxSubtitleEdit.Text;
        s.FFmpeg_Path       = TbxFFmpeg.Text;
        s.FFprobe_Path      = TbxFFprobe.Text;
        s.MKVPropEdit_Path  = TbxMKVPropEdit.Text;
        s.MKVMerge_Path     = TbxMKVMerge.Text;

        // Encode
        s.GpuVendor = CbxGpuVendor.SelectedItem as string ?? "NVIDIA";

        // Read preset from whichever panel is currently active.
        // The inactive panel's combo is ignored — its value may be stale
        // from a previous session with a different vendor.
        var isIntel = s.GpuVendor.Equals("Intel", StringComparison.OrdinalIgnoreCase);
        s.DefaultPreset = isIntel
            ? CbxQsvDefaultPreset.SelectedItem as string ?? "medium"
            : CbxDefaultPreset.SelectedItem    as string ?? "p5";

        s.NvencQualityFlags     = TbxNvencQualityFlags.Text;
        s.QsvQualityFlags       = TbxQsvQualityFlags.Text;
        s.SubRowDefaultBitRate  = CbxSubRowDefaultBitRate.SelectedItem as string ?? "640";
        s.AlwaysConvertToHevc   = ChkAlwaysConvertToHevc.IsChecked == true;

        // Remux
        s.MKVMerge_Defaults = TbxMKVMergeDefaults.Text;
        s.MKVMerge_Options  = TbxMKVMergeOptions.Text;
        s.RoboCopy_Defaults = TbxRoboCopyDefaults.Text;

        // File Names
        s.TitleCaseEnabled        = ChkTitleCase.IsChecked == true;
        s.ResolutionAppendEnabled         = ChkResolutionAppend.IsChecked == true;
        s.ResolutionVerifyAlways          = ChkResolutionVerifyAlways.IsChecked == true;
        s.AutoCorrectResolutionMismatch   = ChkAutoCorrectResolutionMismatch.IsChecked == true;
        s.TitleCaseAcronyms       = TbxAcronyms.Text
            .Split(',')
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .ToList();

        // Run
        s.WriteLogFiles        = ChkWriteLogFiles.IsChecked == true;
        s.MkvMergeVerbose      = ChkMkvMergeVerbose.IsChecked == true;
        s.FfmpegVerboseLogging = ChkFfmpegVerboseLogging.IsChecked == true;
        s.FfmpegLogLevel       = CbxFfmpegLogLevel.SelectedItem as string ?? "verbose";
        s.DisableMoveCompleted = ChkAutoMoveCompleted.IsChecked == true;
        if (int.TryParse(TbxHistorySize.Text, out var histSize) && histSize > 0)
            s.RecentFolderHistorySize = histSize;

        s.Save();
    }

    // ── OK / Cancel ──────────────────────────────────────────────────

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();

        if (!AppSettings.Instance.AllPathsSet())
        {
            MessageBox.Show(
                "Please set all tool paths before continuing.",
                "Application Paths Missing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        // Restore every setting to the snapshot taken at open time.
        // This discards any edits the user made without saving them.
        var s = AppSettings.Instance;
        s.MPV_Path                = _snapshot.MPV_Path;
        s.SubtitleEdit_Path       = _snapshot.SubtitleEdit_Path;
        s.FFmpeg_Path             = _snapshot.FFmpeg_Path;
        s.FFprobe_Path            = _snapshot.FFprobe_Path;
        s.MKVPropEdit_Path        = _snapshot.MKVPropEdit_Path;
        s.MKVMerge_Path           = _snapshot.MKVMerge_Path;
        s.MKVMerge_Defaults       = _snapshot.MKVMerge_Defaults;
        s.MKVMerge_Options        = _snapshot.MKVMerge_Options;
        s.RoboCopy_Defaults       = _snapshot.RoboCopy_Defaults;
        s.AlwaysConvertToHevc     = _snapshot.AlwaysConvertToHevc;
        s.GpuVendor               = _snapshot.GpuVendor;
        s.DefaultPreset           = _snapshot.DefaultPreset;
        s.NvencQualityFlags       = _snapshot.NvencQualityFlags;
        s.QsvQualityFlags         = _snapshot.QsvQualityFlags;
        s.SubRowDefaultBitRate    = _snapshot.SubRowDefaultBitRate;
        s.RecentFolderHistorySize = _snapshot.RecentFolderHistorySize;
        s.TitleCaseEnabled        = _snapshot.TitleCaseEnabled;
        s.TitleCaseAcronyms       = _snapshot.TitleCaseAcronyms
            .Split(',').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList();
        s.ResolutionAppendEnabled         = _snapshot.ResolutionAppendEnabled;
        s.ResolutionVerifyAlways          = _snapshot.ResolutionVerifyAlways;
        s.AutoCorrectResolutionMismatch   = _snapshot.AutoCorrectResolutionMismatch;
        s.WriteLogFiles           = _snapshot.WriteLogFiles;
        s.MkvMergeVerbose         = _snapshot.MkvMergeVerbose;
        s.FfmpegVerboseLogging    = _snapshot.FfmpegVerboseLogging;
        s.FfmpegLogLevel          = _snapshot.FfmpegLogLevel;
        s.DisableMoveCompleted    = _snapshot.DisableMoveCompleted;

        DialogResult = false;
        Close();
    }

    // ── Encode tab — GPU Vendor panel swap ───────────────────────────
    // When the vendor changes, show the matching settings panel and
    // hide the other. The panels are named PnlNvidia and PnlIntel.
    // Visibility.Collapsed removes the panel from layout entirely —
    // it takes no space, which is what we want here.
    private void CbxGpuVendor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard: controls may not be initialised yet during InitializeComponent.
        if (PnlNvidia == null || PnlIntel == null) return;

        var isIntel = CbxGpuVendor.SelectedItem as string == "Intel";
        PnlNvidia.Visibility = isIntel ? Visibility.Collapsed : Visibility.Visible;
        PnlIntel.Visibility  = isIntel ? Visibility.Visible   : Visibility.Collapsed;
    }

    // ── Run tab — logging enable/disable chain ───────────────────────
    // Write Log Files is the master toggle for both logging verbosity
    // checkboxes. mkvmerge verbose applies to remux; ffmpeg verbose
    // applies to transcode. ffmpeg verbose also gates the log level
    // dropdown.

    private void ChkResolutionVerifyAlways_Changed(object sender, RoutedEventArgs e)
    {
        var verifyOn = ChkResolutionVerifyAlways.IsChecked == true;
        ChkAutoCorrectResolutionMismatch.IsEnabled = verifyOn;
        if (!verifyOn)
            ChkAutoCorrectResolutionMismatch.IsChecked = false;
    }

    private void ChkWriteLogFiles_Changed(object sender, RoutedEventArgs e)
    {
        var writeOn = ChkWriteLogFiles.IsChecked == true;
        ChkMkvMergeVerbose.IsEnabled      = writeOn;
        ChkFfmpegVerboseLogging.IsEnabled = writeOn;
        if (!writeOn)
        {
            ChkMkvMergeVerbose.IsChecked      = false;
            ChkFfmpegVerboseLogging.IsChecked = false;
            CbxFfmpegLogLevel.IsEnabled       = false;
        }
    }

    private void ChkMkvMergeVerbose_Changed(object sender, RoutedEventArgs e)
    {
        // No dependent controls — handler exists so the checkbox's
        // Checked/Unchecked events can be wired in XAML symmetrically
        // with the other logging checkboxes. SaveSettings picks up the
        // new value when the dialog is OK'd.
    }

    private void ChkFfmpegVerboseLogging_Changed(object sender, RoutedEventArgs e)
    {
        var verboseOn = ChkFfmpegVerboseLogging.IsChecked == true;
        CbxFfmpegLogLevel.IsEnabled = verboseOn;
        if (!verboseOn)
            CbxFfmpegLogLevel.SelectedItem = "verbose";
    }

    private void ChkAutoMoveCompleted_Changed(object sender, RoutedEventArgs e)
        => SaveSettings();

    // ── Input validation ─────────────────────────────────────────────
    private void HistorySize_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^[0-9]+$");
    }

    // ── Where buttons ────────────────────────────────────────────────
    private void WhereMPV_Click(object sender, RoutedEventArgs e)           => TbxMPV.Text          = RunWhere("mpv");
    private void WhereSubtitleEdit_Click(object sender, RoutedEventArgs e)  => TbxSubtitleEdit.Text = RunWhere("SubtitleEdit");
    private void WhereFFmpeg_Click(object sender, RoutedEventArgs e)        => TbxFFmpeg.Text       = RunWhere("ffmpeg");
    private void WhereFFprobe_Click(object sender, RoutedEventArgs e)       => TbxFFprobe.Text      = RunWhere("ffprobe");
    private void WhereMKVPropEdit_Click(object sender, RoutedEventArgs e)   => TbxMKVPropEdit.Text  = RunWhere("mkvpropedit");
    private void WhereMKVMerge_Click(object sender, RoutedEventArgs e)      => TbxMKVMerge.Text     = RunWhere("mkvmerge");

    // ── Browse buttons ───────────────────────────────────────────────
    private void BrowseMPV_Click(object sender, RoutedEventArgs e)          => TbxMPV.Text          = BrowseForExe() ?? TbxMPV.Text;
    private void BrowseSubtitleEdit_Click(object sender, RoutedEventArgs e) => TbxSubtitleEdit.Text = BrowseForExe() ?? TbxSubtitleEdit.Text;
    private void BrowseFFmpeg_Click(object sender, RoutedEventArgs e)       => TbxFFmpeg.Text       = BrowseForExe() ?? TbxFFmpeg.Text;
    private void BrowseFFprobe_Click(object sender, RoutedEventArgs e)      => TbxFFprobe.Text      = BrowseForExe() ?? TbxFFprobe.Text;
    private void BrowseMKVPropEdit_Click(object sender, RoutedEventArgs e)  => TbxMKVPropEdit.Text  = BrowseForExe() ?? TbxMKVPropEdit.Text;
    private void BrowseMKVMerge_Click(object sender, RoutedEventArgs e)     => TbxMKVMerge.Text     = BrowseForExe() ?? TbxMKVMerge.Text;

    // ── Helpers ──────────────────────────────────────────────────────

    // Runs "where <toolName>" via cmd.exe and returns the first result line,
    // or an empty string if the tool is not on PATH.
    private static string RunWhere(string toolName)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe", $"/C where {toolName}")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(startInfo);
            var result = process?.StandardOutput.ReadLine() ?? "";
            process?.WaitForExit();
            return result.Trim();
        }
        catch { return ""; }
    }

    // Opens a file browser dialog and returns the selected path, or null on cancel.
    private static string? BrowseForExe()
    {
        var dialog = new OpenFileDialog
        {
            Title            = "Find Program Path",
            InitialDirectory = @"C:\",
            Filter           = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            FilterIndex      = 1,
            RestoreDirectory = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
