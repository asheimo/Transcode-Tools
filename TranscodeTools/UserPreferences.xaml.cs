// ============================================================
// UserPreferences.xaml.cs
// ------------------------------------------------------------
// Code-behind for the User Preferences window.
// Handles loading settings into the form, saving them back,
// and the Where/Browse button logic for each tool path.
// ============================================================

using System.Diagnostics;  // For Process (running "where" command)
using System.Windows;
using Microsoft.Win32;     // For OpenFileDialog

namespace TranscodeTools;

public partial class UserPreferences : Window
{
    // ── Constructor ──────────────────────────────────────────────────
    public UserPreferences()
    {
        InitializeComponent();

        // Load the current saved settings into the text boxes
        // as soon as the window is created.
        LoadSettings();
    }

    // ── Load / Save ──────────────────────────────────────────────────

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
        TbxOtherTranscode.Text        = s.OtherTranscode_Path;
        TbxMKVPropEdit.Text           = s.MKVPropEdit_Path;
        TbxMKVMerge.Text              = s.MKVMerge_Path;
        TbxRuby.Text                  = s.Ruby_Path;
        TbxOtherTranscodeDefaults.Text = s.OtherTranscode_Defaults;
        TbxOtherTranscodeOptions.Text  = s.OtherTranscode_Options;
        TbxMKVMergeDefaults.Text      = s.MKVMerge_Defaults;
        TbxMKVMergeOptions.Text       = s.MKVMerge_Options;
        TbxRoboCopyDefaults.Text      = s.RoboCopy_Defaults;
    }

    // Writes all text box values back to AppSettings and saves to disk.
    private void SaveSettings()
    {
        var s = AppSettings.Instance;

        s.MPV_Path                = TbxMPV.Text;
        s.SubtitleEdit_Path       = TbxSubtitleEdit.Text;
        s.FFmpeg_Path             = TbxFFmpeg.Text;
        s.FFprobe_Path            = TbxFFprobe.Text;
        s.OtherTranscode_Path     = TbxOtherTranscode.Text;
        s.MKVPropEdit_Path        = TbxMKVPropEdit.Text;
        s.MKVMerge_Path           = TbxMKVMerge.Text;
        s.Ruby_Path               = TbxRuby.Text;
        s.OtherTranscode_Defaults = TbxOtherTranscodeDefaults.Text;
        s.OtherTranscode_Options  = TbxOtherTranscodeOptions.Text;
        s.MKVMerge_Defaults       = TbxMKVMergeDefaults.Text;
        s.MKVMerge_Options        = TbxMKVMergeOptions.Text;
        s.RoboCopy_Defaults       = TbxRoboCopyDefaults.Text;

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
        // Close without saving — settings remain as they were before
        // the window was opened.
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

    private void WhereOtherTranscode_Click(object sender, RoutedEventArgs e)
        => TbxOtherTranscode.Text = RunWhere("other-transcode");

    private void WhereMKVPropEdit_Click(object sender, RoutedEventArgs e)
        => TbxMKVPropEdit.Text = RunWhere("mkvpropedit");

    private void WhereMKVMerge_Click(object sender, RoutedEventArgs e)
        => TbxMKVMerge.Text = RunWhere("mkvmerge");

    private void WhereRuby_Click(object sender, RoutedEventArgs e)
        => TbxRuby.Text = RunWhere("ruby");

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

    private void BrowseOtherTranscode_Click(object sender, RoutedEventArgs e)
        => TbxOtherTranscode.Text = BrowseForExe() ?? TbxOtherTranscode.Text;

    private void BrowseMKVPropEdit_Click(object sender, RoutedEventArgs e)
        => TbxMKVPropEdit.Text = BrowseForExe() ?? TbxMKVPropEdit.Text;

    private void BrowseMKVMerge_Click(object sender, RoutedEventArgs e)
        => TbxMKVMerge.Text = BrowseForExe() ?? TbxMKVMerge.Text;

    private void BrowseRuby_Click(object sender, RoutedEventArgs e)
        => TbxRuby.Text = BrowseForExe() ?? TbxRuby.Text;

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
