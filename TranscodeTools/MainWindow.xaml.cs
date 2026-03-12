// ============================================================
// MainWindow.xaml.cs
// ------------------------------------------------------------
// This is the CODE-BEHIND for the main window.
// It pairs with MainWindow.xaml — the XAML defines WHAT the
// UI looks like, and this file defines HOW it behaves.
//
// Think of it like a VB.NET Form: the designer sets up the
// controls, and the .vb file handles the events and logic.
// ============================================================

using System.Collections.ObjectModel; // For ObservableCollection
using System.IO;                       // For Directory, Path, File
using System.Windows;                  // For Window, MessageBox, Visibility
using System.Windows.Controls;        // For ListBox, SelectionChangedEventArgs
using Microsoft.Win32;                 // For OpenFolderDialog

namespace TranscodeTools;

// "partial" because the other half of this class is auto-generated
// from MainWindow.xaml by the XAML compiler.
public partial class MainWindow : Window
{
    // ── Observable Collections ───────────────────────────────────────
    //
    // ObservableCollection<T> is like a List in VB.NET, but it
    // automatically notifies the UI when items are added or removed.
    // The <T> is a GENERIC TYPE PARAMETER — T is replaced with the
    // actual type you want to store, e.g. ObservableCollection<RemuxVideoTrack>
    // means "a collection of RemuxVideoTrack objects".
    //
    // "public ... { get; } = new();" declares a property with only a getter
    // (read-only from outside), initialised immediately with a new instance.
    // new() is shorthand for new ObservableCollection<RemuxVideoTrack>() —
    // C# can infer the type from the property declaration.

    // --- Remux mode collections ---
    public ObservableCollection<RemuxVideoTrack>    RemuxVideoTracks    { get; } = new();
    public ObservableCollection<RemuxAudioTrack>    RemuxAudioTracks    { get; } = new();
    public ObservableCollection<RemuxSubtitleTrack> RemuxSubtitleTracks { get; } = new();

    // --- Transcode mode collections ---
    public ObservableCollection<TranscodeVideoTrack>    TranscodeVideoTracks    { get; } = new();
    public ObservableCollection<TranscodeAudioTrack>    TranscodeAudioTracks    { get; } = new();
    public ObservableCollection<TranscodeSubtitleTrack> TranscodeSubtitleTracks { get; } = new();

    // Private field to track which mode we're in.
    // The underscore prefix is a C# convention for private instance fields.
    // In VB.NET: Private _isTranscodeMode As Boolean = False
    private bool _isTranscodeMode = false;


    // ── Constructor ──────────────────────────────────────────────────
    //
    // The constructor runs once when the window is created.
    // It has the same name as the class — this is a C# rule.
    // In VB.NET this would be: Public Sub New()
    public MainWindow()
    {
        // InitializeComponent() reads the XAML and builds all the controls.
        // You must always call this first — it's the equivalent of the
        // auto-generated InitializeComponent() call in a VB.NET Form.
        InitializeComponent();

        // Connect each ListView to its data collection.
        // ItemsSource tells WPF "show the items from this collection in this list."
        // When the collection changes, the list updates automatically.
        RemuxVideoList.ItemsSource        = RemuxVideoTracks;
        RemuxAudioList.ItemsSource        = RemuxAudioTracks;
        RemuxSubtitleList.ItemsSource     = RemuxSubtitleTracks;
        TranscodeVideoList.ItemsSource    = TranscodeVideoTracks;
        TranscodeAudioList.ItemsSource    = TranscodeAudioTracks;
        TranscodeSubtitleList.ItemsSource = TranscodeSubtitleTracks;

        // Fill the lists with sample data so the UI isn't empty on first launch.
        // Remove this call (or clear the method) when you hook up real MediaInfo data.
        LoadSampleData();

        // Check all tool paths are configured — prompts user to set them if not.
        ValidatePathsOnStartup();
    }


    // ── Mode Switch ──────────────────────────────────────────────────
    //
    // This event handler fires when either the Remux or Transcode
    // radio button is checked. Both radio buttons point to this same handler
    // (set in the XAML with Checked="ModeChanged").

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        // IMPORTANT: ModeChanged fires during InitializeComponent() when the
        // radio buttons are first created. At that point TranscodeRadio may
        // not exist yet, causing a null reference error.
        // This guard returns early if the control isn't ready yet,
        // leaving the XAML default (Remux visible) in place.
        // "is null" is the modern C# way to check for null.
        // In VB.NET: If TranscodeRadio Is Nothing Then Return
        if (TranscodeRadio is null) return;

        // IsChecked returns a bool? (nullable bool) in WPF because a RadioButton
        // can technically be in an indeterminate state. Comparing == true safely
        // handles the case where IsChecked is null.
        _isTranscodeMode = TranscodeRadio.IsChecked == true;

        // Show the correct panel and hide the other.
        // The ternary operator (?:) is used here:
        //   condition ? valueIfTrue : valueIfFalse
        RemuxPanel.Visibility     = _isTranscodeMode ? Visibility.Collapsed : Visibility.Visible;
        TranscodePanel.Visibility = _isTranscodeMode ? Visibility.Visible   : Visibility.Collapsed;

        // Build an array of the three buttons that are Remux-only.
        // new[] { ... } creates an array and infers the type automatically.
        // In VB.NET: Dim btnRemux() As Button = { OpenInMPVBtn, ... }
        var btnRemux = new[] { OpenInMPVBtn, OpenInSubtitleEditBtn, OpenFolderBtn };

        // In Transcode mode: hide remux buttons, show Save Transcode button.
        // In Remux mode: show remux buttons, hide Save Transcode button.
        if (SaveTranscodeBtn == null) return;

        SaveTranscodeBtn.Visibility = _isTranscodeMode ? Visibility.Visible : Visibility.Collapsed;

        // Loop through the remux-only buttons and toggle their visibility.
        // "foreach" in C# is the same as "For Each" in VB.NET.
        foreach (var btn in btnRemux)
            if (btn != null)
                btn.Visibility = _isTranscodeMode ? Visibility.Collapsed : Visibility.Visible;
    }


    // ── Directory Selection ──────────────────────────────────────────

    private void OpenInputDirectory_Click(object sender, RoutedEventArgs e)
    {
        // BrowseFolder returns null if the user cancelled the dialog.
        // The "var" keyword lets C# infer the type — here it infers string?
        var path = BrowseFolder("Select Input Directory");

        // "return" with no value exits the method early — same as Exit Sub in VB.NET
        if (path == null) return;

        InputDirectoryBox.Text = path;
        LoadFilesFromDirectory(path, InputFileList);
    }

    private void OpenOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFolder("Select Output Directory");
        if (path == null) return;

        OutputDirectoryBox.Text = path;
        LoadFilesFromDirectory(path, OutputFileList);
    }

    // "private static" — static means this method doesn't need access to
    // any instance data (no "this"), so it belongs to the class itself.
    // The string? return type means it can return a string OR null.
    // In VB.NET: Private Shared Function BrowseFolder(title As String) As String
    private static string? BrowseFolder(string title)
    {
        // OpenFolderDialog is a WPF built-in (available in .NET 8+).
        // The { Title = title } syntax is an OBJECT INITIALISER —
        // a shorthand for creating an object and setting its properties.
        // In VB.NET: Dim dialog As New OpenFolderDialog() : dialog.Title = title
        var dialog = new OpenFolderDialog { Title = title };

        // ShowDialog() returns a nullable bool (bool?). We compare == true
        // for the same reason as IsChecked above.
        // The ternary operator returns the folder path or null.
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static void LoadFilesFromDirectory(string path, ListBox list)
    {
        list.Items.Clear();

        // try/catch in C# is the same as Try/Catch in VB.NET.
        try
        {
            // LINQ chain — each method transforms the data one step at a time.
            // This is similar to using multiple VB.NET LINQ extension methods.
            var files = Directory.GetFiles(path)   // Get all file paths in the folder
                .Select(Path.GetFileName)           // Extract just the filename from each path
                .Where(f => f != null)              // Filter out any null results
                .OrderBy(f => f);                   // Sort alphabetically

            // foreach loop — same as For Each in VB.NET
            foreach (var f in files)
                list.Items.Add(f);
        }
        catch (Exception ex)
        {
            // $"..." is string interpolation — same as $"..." in modern VB.NET.
            // \n is a newline character (in VB.NET you'd use vbNewLine or Environment.NewLine)
            MessageBox.Show($"Could not read directory:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    // ── File Selection → Populate Tracks ────────────────────────────

    private void InputFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // "is not string fileName" is a PATTERN MATCH with negation.
        // It checks if SelectedItem is NOT a string, and returns early if so.
        // This also handles the null case. In VB.NET:
        //   If Not TypeOf InputFileList.SelectedItem Is String Then Return
        //   Dim fileName As String = CStr(InputFileList.SelectedItem)
        if (InputFileList.SelectedItem is not string fileName) return;

        // ToLowerInvariant() converts to lowercase using culture-invariant rules
        // (safe for file extensions regardless of the user's language settings)
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        PopulateTracksForFile(fileName, ext);
    }

    private void PopulateTracksForFile(string fileName, string ext)
    {
        // Clear all six collections before loading new data.
        // This ensures stale data from a previous selection is removed.
        RemuxVideoTracks.Clear();
        RemuxAudioTracks.Clear();
        RemuxSubtitleTracks.Clear();
        TranscodeVideoTracks.Clear();
        TranscodeAudioTracks.Clear();
        TranscodeSubtitleTracks.Clear();

        // Add placeholder track data. In a real application you would call
        // MediaInfo.dll or run "ffprobe -v quiet -print_format json -show_streams"
        // and parse the JSON output to populate these tracks automatically.

        // OBJECT INITIALISER syntax — sets multiple properties in one block.
        // In VB.NET:
        //   Dim t As New RemuxVideoTrack()
        //   t.VideoFormat = "H.264 / AVC"
        //   ...
        //   RemuxVideoTracks.Add(t)
        RemuxVideoTracks.Add(new RemuxVideoTrack
        {
            VideoFormat = "H.264 / AVC",
            Resolution  = "1920x1080",
            Fps         = "23.976",
            IsDefault   = true
        });

        RemuxAudioTracks.Add(new RemuxAudioTrack
        {
            AudioFormat = "AAC",
            Width       = "2ch",
            BitRate     = "192",
            Language    = "eng",
            Title       = "English",
            IsDefault   = true,
            TrackId     = "2"
        });

        RemuxSubtitleTracks.Add(new RemuxSubtitleTrack
        {
            SubtitleFormat = "ASS",
            Language       = "eng",
            FrameCount     = "842",
            Title          = "English",
            TrackId        = "3"
        });

        // $ before a string means STRING INTERPOLATION — variables inside {}
        // are evaluated and inserted into the string. Same as $"..." in VB.NET.
        TranscodeVideoTracks.Add(new TranscodeVideoTrack
        {
            TrackInfo    = $"Video: H.264 1920x1080 @ 23.976fps",
            Resolution   = "1920x1080",
            OutputFormat = "H.265 / HEVC",
            FrameRate    = "23.976"
        });

        TranscodeAudioTracks.Add(new TranscodeAudioTrack
        {
            TrackInfo = "Audio: AAC 2ch 192Kbps [English]",
            Format    = "AAC",
            Width     = "2ch",
            BitRate   = "192"
        });

        TranscodeSubtitleTracks.Add(new TranscodeSubtitleTrack
        {
            TrackInfo = "Subtitle: ASS [English]",
            IsDefault = true
        });
    }


    // ── Bottom Button Handlers ───────────────────────────────────────

    private void OpenInMPV_Click(object sender, RoutedEventArgs e)
    {
        // Pattern match — only continues if SelectedItem is a string
        if (InputFileList.SelectedItem is not string file) return;

        // Path.Combine safely joins folder + filename with the correct separator.
        // Equivalent to IO.Path.Combine in VB.NET.
        var fullPath = Path.Combine(InputDirectoryBox.Text, file);
        TryLaunch("mpv", fullPath);
    }

    private void OpenInSubtitleEdit_Click(object sender, RoutedEventArgs e)
    {
        if (InputFileList.SelectedItem is not string file) return;
        var fullPath = Path.Combine(InputDirectoryBox.Text, file);
        TryLaunch("SubtitleEdit", fullPath);
    }

    private void OpenFolderInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var path = InputDirectoryBox.Text;

        // IsNullOrWhiteSpace checks for null, empty string, or just spaces.
        // Equivalent to String.IsNullOrWhiteSpace in VB.NET.
        if (string.IsNullOrWhiteSpace(path)) return;

        System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            _isTranscodeMode ? "Transcode settings saved." : "Remux settings saved.",
            "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }


    // ── Startup validation ───────────────────────────────────────────

    // Called from the constructor after InitializeComponent().
    // If any tool paths are missing, open Preferences immediately
    // so the user can set them — same behaviour as the original app.
    private void ValidatePathsOnStartup()
    {
        if (!AppSettings.Instance.AllPathsSet())
        {
            MessageBox.Show(
                "Please set all tool paths before continuing.",
                "Application Paths Missing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            OpenPreferences();
        }
    }

    // Opens the UserPreferences window as a modal dialog.
    // "Owner = this" centres it over the main window.
    private void OpenPreferences()
    {
        var prefs = new UserPreferences { Owner = this };
        prefs.ShowDialog();
        // ShowDialog() blocks until the window is closed.
        // Any changes the user made are already saved to AppSettings
        // by the time we get back here.
    }

    // ── Menu Handlers ────────────────────────────────────────────────

    // The => here is an EXPRESSION-BODIED METHOD — a one-liner shorthand.
    // It's equivalent to: private void Exit_Click(...) { Close(); }
    // In VB.NET: Private Sub Exit_Click(...) : Me.Close() : End Sub
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Transcode Tools\nModern WPF Edition", "About",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
        => OpenPreferences();


    // ── Helper Methods ───────────────────────────────────────────────

    // Attempts to launch an external application with a file argument.
    // Wrapped in try/catch so a missing app shows a friendly message
    // rather than crashing.
    private static void TryLaunch(string exe, string arg)
    {
        try
        {
            // Wrapping arg in quotes handles file paths that contain spaces.
            System.Diagnostics.Process.Start(exe, $"\"{arg}\"");
        }
        catch
        {
            MessageBox.Show($"Could not launch {exe}.\nMake sure it is installed and on PATH.",
                "Launch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Populates the ListViews with sample data so the UI looks meaningful
    // on first launch. Replace this with real MediaInfo/ffprobe calls.
    private void LoadSampleData()
    {
        // Multiple items added to show what a real populated list looks like.
        // Notice the compact single-line object initialiser syntax —
        // valid C# when all properties fit on one line.
        RemuxVideoTracks.Add(new RemuxVideoTrack
            { VideoFormat = "H.264 / AVC", Resolution = "1920x1080", Fps = "23.976", IsDefault = true });

        RemuxAudioTracks.Add(new RemuxAudioTrack
            { AudioFormat = "DTS-HD MA", Width = "7.1ch", BitRate = "4608", Language = "eng", Title = "English", IsDefault = true, TrackId = "2" });
        RemuxAudioTracks.Add(new RemuxAudioTrack
            { AudioFormat = "AAC", Width = "2ch", BitRate = "192", Language = "jpn", Title = "Japanese", TrackId = "3" });

        RemuxSubtitleTracks.Add(new RemuxSubtitleTrack
            { SubtitleFormat = "ASS", Language = "eng", FrameCount = "1204", Title = "English", IsDefault = true, TrackId = "4" });
        RemuxSubtitleTracks.Add(new RemuxSubtitleTrack
            { SubtitleFormat = "PGS", Language = "eng", FrameCount = "312", Title = "English Forced", IsForced = true, TrackId = "5" });

        TranscodeVideoTracks.Add(new TranscodeVideoTrack
            { TrackInfo = "Video: H.264 1920x1080 @ 23.976fps", Resolution = "1920x1080", OutputFormat = "H.265 / HEVC", FrameRate = "23.976" });

        TranscodeAudioTracks.Add(new TranscodeAudioTrack
            { TrackInfo = "Audio: DTS-HD MA 7.1ch 4608Kbps [English]", Format = "AAC", Width = "5.1ch", BitRate = "640" });

        TranscodeSubtitleTracks.Add(new TranscodeSubtitleTrack
            { TrackInfo = "Subtitle: ASS [English]", IsDefault = true });
    }
}
