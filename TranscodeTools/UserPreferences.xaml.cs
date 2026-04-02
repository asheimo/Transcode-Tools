// ============================================================
// UserPreferences.xaml.cs
// ------------------------------------------------------------
// Code-behind for the User Preferences window.
// Handles loading settings into the form, saving them back,
// and the Where/Browse button logic for each tool path.
// ============================================================

using System.Diagnostics;  // For Process (running "where" command)
using System.Windows;
using System.Windows.Controls;  // For TextBox
using System.Windows.Input;     // For KeyboardFocusChangedEventArgs
using System.Text.RegularExpressions; // For numeric-only input validation
using Microsoft.Win32;          // For OpenFileDialog

namespace TranscodeTools;

public partial class UserPreferences : Window
{
    // ── Snapshot for Cancel ──────────────────────────────────────────
    // We take a copy of all settings when the window opens.
    // If the user clicks Cancel, we restore this snapshot so that any
    // changes they made in the text boxes are thrown away.
    //
    // Without this, editing a path and then clicking Cancel would leave
    // AppSettings.Instance with the half-edited value even though nothing
    // was saved to disk — because the text boxes write directly to the
    // instance when SaveSettings() is called, but Cancel never called
    // SaveSettings() so the in-memory object still held the dirty value.
    //
    // A record is a perfect fit here — it's an immutable snapshot.
    // We create it once at open time and never change it.
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
        int    RecentFolderHistorySize
    );

    private readonly SettingsSnapshot _snapshot;

    // ── Constructor ──────────────────────────────────────────────────
    public UserPreferences()
    {
        InitializeComponent();

        // Take the snapshot BEFORE loading settings into the text boxes,
        // so we capture the values exactly as they were when the window opened.
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
            s.RecentFolderHistorySize
        );

        // Load the current saved settings into the text boxes.
        LoadSettings();
    }

    // ── TextBox click-to-select-all ──────────────────────────────────
    // This handler fires whenever any control inside the window receives
    // keyboard focus — including when the user clicks a TextBox.
    //
    // We check if the focused element is a TextBox, and if so call
    // SelectAll() to highlight its entire contents.
    //
    // Because this is on the Window rather than each individual TextBox,
    // one handler covers every path box automatically — no need to wire
    // up the same event thirteen times.
    //
    // GotKeyboardFocus rather than GotFocus is used because it fires
    // reliably for both mouse clicks and Tab key navigation.
    // In VB.NET WinForms the equivalent was handling the Enter event
    // on each TextBox individually and calling SelectAll() there.
    private void Window_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // e.NewFocus is the element that just received focus.
        // "is TextBox tb" pattern-matches and assigns it to tb in one step.
        if (e.NewFocus is TextBox tb)
            tb.SelectAll();
    }

    // Reads from AppSettings.Instance and populates all text boxes.
    // AppSettings.Instance is our singleton — the one shared settings
    // object for the whole application.
    private void LoadSettings()
    {
        // Shorthand variable so we don't have to type AppSettings.Instance
        // every single line. "var" infers the type as AppSettings.
        var s = AppSettings.Instance;

        TbxMPV.Text                   = s.MPV_Path;
        TbxSubtitleEdit.Text          = s.SubtitleEdit_Path;
        TbxFFmpeg.Text                = s.FFmpeg_Path;
        TbxFFprobe.Text               = s.FFprobe_Path;
        TbxMKVPropEdit.Text           = s.MKVPropEdit_Path;
        TbxMKVMerge.Text              = s.MKVMerge_Path;
        TbxMKVMergeDefaults.Text      = s.MKVMerge_Defaults;
        TbxMKVMergeOptions.Text       = s.MKVMerge_Options;
        TbxRoboCopyDefaults.Text      = s.RoboCopy_Defaults;
        ChkAlwaysConvertToHevc.IsChecked = s.AlwaysConvertToHevc;
        TbxHistorySize.Text               = s.RecentFolderHistorySize.ToString();
    }

    // Writes all text box values back to AppSettings and saves to disk.
    private void SaveSettings()
    {
        var s = AppSettings.Instance;

        s.MPV_Path                = TbxMPV.Text;
        s.SubtitleEdit_Path       = TbxSubtitleEdit.Text;
        s.FFmpeg_Path             = TbxFFmpeg.Text;
        s.FFprobe_Path            = TbxFFprobe.Text;
        s.MKVPropEdit_Path        = TbxMKVPropEdit.Text;
        s.MKVMerge_Path           = TbxMKVMerge.Text;
        s.MKVMerge_Defaults       = TbxMKVMergeDefaults.Text;
        s.MKVMerge_Options        = TbxMKVMergeOptions.Text;
        s.RoboCopy_Defaults       = TbxRoboCopyDefaults.Text;
        s.AlwaysConvertToHevc     = ChkAlwaysConvertToHevc.IsChecked == true;
        // Parse history size — fall back to current value if the box is empty or invalid
        if (int.TryParse(TbxHistorySize.Text, out var histSize) && histSize > 0)
            s.RecentFolderHistorySize = histSize;

        // Persist to disk — writes settings.json in AppData\Roaming\TranscodeTools
        s.Save();
    }

    // ── OK / Cancel ──────────────────────────────────────────────────

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();

        // Check that all required tool paths have been filled in.
        // AllPathsSet() is a helper method on AppSettings that checks
        // every mandatory path is non-empty.
        if (!AppSettings.Instance.AllPathsSet())
        {
            MessageBox.Show(
                "Please set all tool paths before continuing.",
                "Application Paths Missing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            // Don't close the window — let the user fix the missing paths.
            // "return" exits this method early, same as Exit Sub in VB.NET.
            return;
        }

        // All paths are set — close the window.
        // DialogResult = true signals to the caller that the user clicked OK
        // (as opposed to Cancel). The caller can check this if needed.
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        // Restore AppSettings.Instance to exactly what it was when the
        // window opened. This discards any edits the user made in the
        // text boxes without saving them — true Cancel behaviour.
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
        s.AlwaysConvertToHevc        = _snapshot.AlwaysConvertToHevc;
        s.RecentFolderHistorySize    = _snapshot.RecentFolderHistorySize;

        DialogResult = false;
        Close();
    }

    // ── "Where" buttons ──────────────────────────────────────────────
    // Each Where button runs "where <toolname>" via cmd.exe and puts
    // the result into the corresponding text box.
    // If the tool isn't on PATH, RunWhere returns an empty string.

    private void WhereMPV_Click(object sender, RoutedEventArgs e)
        => TbxMPV.Text = RunWhere("mpv");

    private void WhereSubtitleEdit_Click(object sender, RoutedEventArgs e)
        => TbxSubtitleEdit.Text = RunWhere("SubtitleEdit");

    private void WhereFFmpeg_Click(object sender, RoutedEventArgs e)
        => TbxFFmpeg.Text = RunWhere("ffmpeg");

    private void WhereFFprobe_Click(object sender, RoutedEventArgs e)
        => TbxFFprobe.Text = RunWhere("ffprobe");

    private void WhereMKVPropEdit_Click(object sender, RoutedEventArgs e)
        => TbxMKVPropEdit.Text = RunWhere("mkvpropedit");

    private void WhereMKVMerge_Click(object sender, RoutedEventArgs e)
        => TbxMKVMerge.Text = RunWhere("mkvmerge");

    // ── "Browse" buttons ─────────────────────────────────────────────
    // Each Browse button opens a file picker so the user can navigate
    // to the executable manually.

    private void BrowseMPV_Click(object sender, RoutedEventArgs e)
        => TbxMPV.Text = BrowseForExe() ?? TbxMPV.Text;
    //                                   ^ If BrowseForExe returns null
    //                                     (user cancelled), keep the
    //                                     existing value unchanged.

    private void BrowseSubtitleEdit_Click(object sender, RoutedEventArgs e)
        => TbxSubtitleEdit.Text = BrowseForExe() ?? TbxSubtitleEdit.Text;

    private void BrowseFFmpeg_Click(object sender, RoutedEventArgs e)
        => TbxFFmpeg.Text = BrowseForExe() ?? TbxFFmpeg.Text;

    private void BrowseFFprobe_Click(object sender, RoutedEventArgs e)
        => TbxFFprobe.Text = BrowseForExe() ?? TbxFFprobe.Text;

    private void BrowseMKVPropEdit_Click(object sender, RoutedEventArgs e)
        => TbxMKVPropEdit.Text = BrowseForExe() ?? TbxMKVPropEdit.Text;

    private void BrowseMKVMerge_Click(object sender, RoutedEventArgs e)
        => TbxMKVMerge.Text = BrowseForExe() ?? TbxMKVMerge.Text;

    // ── Input validation ─────────────────────────────────────────────
    // Prevents non-numeric characters being typed into the History Size box.
    private void HistorySize_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^[0-9]+$");
    }

    // ── Helper methods ───────────────────────────────────────────────

    // Runs "where <toolName>" via cmd.exe and returns the first line
    // of output (the full path), or an empty string if not found.
    // "static" because it doesn't need any instance data.
    private static string RunWhere(string toolName)
    {
        try
        {
            // ProcessStartInfo configures how to launch an external process.
            // This is the same pattern as the original VB.NET RunCommandCom().
            var startInfo = new ProcessStartInfo("cmd.exe", $"/C where {toolName}")
            {
                // These three settings let us capture the output as text
                // rather than showing a console window to the user.
                CreateNoWindow        = true,
                UseShellExecute       = false,
                RedirectStandardOutput = true
            };

            // Start the process, read the first line of output, then
            // wait for it to finish.
            using var process = Process.Start(startInfo);
            // ReadLine() returns null if there's no output (tool not found).
            // ?? "" converts null to an empty string.
            var result = process?.StandardOutput.ReadLine() ?? "";
            process?.WaitForExit();
            return result.Trim();
        }
        catch
        {
            // If anything goes wrong (cmd.exe not found etc.) return empty.
            return "";
        }
    }

    // Opens a file browser dialog and returns the selected file path,
    // or null if the user cancelled.
    // string? means the return type is a nullable string.
    private static string? BrowseForExe()
    {
        var dialog = new OpenFileDialog
        {
            Title            = "Find Program Path",
            InitialDirectory = @"C:\",
            // Filter format: "Display name|*.extension|..."
            // This shows all files, same as the original VB.NET version.
            Filter           = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            FilterIndex      = 1,
            RestoreDirectory = true
        };

        // ShowDialog() returns true if the user selected a file.
        // == true handles the nullable bool return, same pattern as elsewhere.
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
