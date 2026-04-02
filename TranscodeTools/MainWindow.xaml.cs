// ============================================================
// MainWindow.xaml.cs
// ============================================================

using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace TranscodeTools;

public partial class MainWindow : Window
{
    // ── Track collections ────────────────────────────────────────────
    public ObservableCollection<RemuxVideoTrack>    RemuxVideoTracks    { get; } = new();
    public ObservableCollection<RemuxAudioTrack>    RemuxAudioTracks    { get; } = new();
    public ObservableCollection<RemuxSubtitleTrack> RemuxSubtitleTracks { get; } = new();
    public ObservableCollection<TranscodeVideoTrack>    TranscodeVideoTracks    { get; } = new();
    public ObservableCollection<TranscodeAudioTrack>    TranscodeAudioTracks    { get; } = new();
    public ObservableCollection<TranscodeSubtitleTrack> TranscodeSubtitleTracks { get; } = new();

    // ── Transcode dropdown option lists ─────────────────────────────
    // Static lists used as ItemsSource for the ComboBoxes in the Transcode
    // video and audio tables. Defined here so the XAML can bind to them via
    // RelativeSource. In VB.NET WinForms these were hard-coded into the
    // DataGridViewComboBoxColumn.Items collection in the Designer.
    public static readonly IReadOnlyList<string> VideoResolutionOptions =
        ["Keep", "480p", "720p", "1080p", "2160p"];
    public static readonly IReadOnlyList<string> VideoOutputFormatOptions =
        ["hevc (default)", "h.264"];
    public static readonly IReadOnlyList<string> VideoFrameRateOptions =
        ["Keep", "30000/1001", "24000/1001"];
    public static readonly IReadOnlyList<string> AudioFormatOptions =
        ["Keep", "eac3", "ac3"];
    public static readonly IReadOnlyList<string> AudioWidthOptions =
        ["Keep", "Surround", "Stereo", "Mono"];
    public static readonly IReadOnlyList<string> AudioBitRateOptions =
        ["Keep", "1536", "768", "640", "320", "192"];

    // ── File browser state ───────────────────────────────────────────
    // The currently selected input folder path, e.g. "H:\Video"
    private string _inputDirectory = "";

    // The currently selected movie subfolder name, e.g. "Casino Royale (2006)"
    private string _selectedMovieFolder = "";

    // Drag-and-drop state for the audio and subtitle ListViews
    // Stores the index of the row being dragged
    private int _dragFromIndex = -1;

    // Which list is being dragged (so MouseMove knows which list to act on)
    private ListView? _dragSourceList;

    private bool _isTranscodeMode = false;

    // Known extras category suffixes — split on the LAST dash in the filename,
    // then check if what follows is one of these known types.
    // This fixes the original bug where filenames containing dashes
    // (e.g. "Ian Fleming - Secret Road To Paradise-featurette") were
    // misidentified because the code split on the first dash instead of the last.
    private static readonly HashSet<string> KnownCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        // HashSet<string> gives O(1) lookup — faster than checking a list one by one.
        // StringComparer.OrdinalIgnoreCase makes the comparison case-insensitive.
        "featurette", "trailer", "behindthescenes", "deleted",
        "interview", "other", "scene", "short"
    };

    // ── Constructor ──────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();

        RemuxVideoList.ItemsSource        = RemuxVideoTracks;
        RemuxAudioList.ItemsSource        = RemuxAudioTracks;
        RemuxSubtitleList.ItemsSource     = RemuxSubtitleTracks;
        TranscodeVideoList.ItemsSource    = TranscodeVideoTracks;
        TranscodeAudioList.ItemsSource    = TranscodeAudioTracks;
        TranscodeSubtitleList.ItemsSource = TranscodeSubtitleTracks;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshHistoryDropdowns();
        ValidatePathsOnStartup();
    }

    // ── Mode switch ──────────────────────────────────────────────────
    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (TranscodeRadio is null) return;
        _isTranscodeMode = TranscodeRadio.IsChecked == true;

        RemuxPanel.Visibility     = _isTranscodeMode ? Visibility.Collapsed : Visibility.Visible;
        TranscodePanel.Visibility = _isTranscodeMode ? Visibility.Visible   : Visibility.Collapsed;

        var btnRemux = new[] { OpenInMPVBtn, OpenInSubtitleEditBtn, OpenFolderBtn };
        if (SaveTranscodeBtn == null) return;

        SaveRemuxBtn.Visibility            = _isTranscodeMode ? Visibility.Collapsed : Visibility.Visible;
        SaveTranscodeBtn.Visibility        = _isTranscodeMode ? Visibility.Visible   : Visibility.Collapsed;
        ViewRemuxCommandBtn.Visibility     = _isTranscodeMode ? Visibility.Collapsed : Visibility.Visible;
        ViewTranscodeCommandBtn.Visibility = _isTranscodeMode ? Visibility.Visible   : Visibility.Collapsed;
        RunMenuItem.Header                 = _isTranscodeMode ? "Run Transcode"       : "Run Remux";

        // Remux View Command is only valid when a settings file exists.
        // Reset to disabled on mode switch — it will be re-enabled when a
        // leaf with a green background is selected.
        ViewRemuxCommandBtn.IsEnabled = false;

        foreach (var btn in btnRemux)
            if (btn != null)
                btn.Visibility = _isTranscodeMode ? Visibility.Collapsed : Visibility.Visible;

        // Full reset on mode switch — same behaviour as the original app.
        // The input/output directories will differ between Remux and Transcode
        // so everything needs to be cleared for a fresh start.
        _inputDirectory      = "";
        _selectedMovieFolder = "";
        InputDirectoryBox.Text  = "";
        OutputDirectoryBox.Text = "";
        FolderList.Items.Clear();
        FileTree.Items.Clear();
        ClearTrackTables();
        HideSelectedFileBar();
    }

    // ── Directory selection ──────────────────────────────────────────
    private void OpenInputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFolder("Select Input Directory");
        if (path == null) return;

        LoadInputDirectory(path);
    }

    private void OpenOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFolder("Select Output Directory");
        if (path == null) return;

        LoadOutputDirectory(path);
    }

    // ── History dropdown handlers ────────────────────────────────────
    // Fire when the user selects an item from the recent folders dropdown.
    // _suppressDirectorySelection guards against the handler firing when
    // we programmatically set the ItemsSource (which triggers SelectionChanged).
    private bool _suppressDirectorySelection = false;

    private void InputDirectoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDirectorySelection) return;
        if (InputDirectoryBox.SelectedItem is not string selected) return;
        LoadInputDirectory(selected);
    }

    private void OutputDirectoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDirectorySelection) return;
        if (OutputDirectoryBox.SelectedItem is not string selected) return;
        LoadOutputDirectory(selected);
    }

    // ── Directory load helpers ───────────────────────────────────────
    // Shared logic for both Browse and history selection so both paths
    // behave identically.

    private void LoadInputDirectory(string folder)
    {
        _inputDirectory = folder;
        AppSettings.Instance.AddRecentInput(folder);
        AppSettings.Instance.Save();
        RefreshHistoryDropdowns();

        // Set Text after refreshing so the ComboBox shows the selected path
        InputDirectoryBox.Text = folder;
        LoadMovieFolders(folder);

        FileTree.Items.Clear();
        ClearTrackTables();
        HideSelectedFileBar();
    }

    private void LoadOutputDirectory(string folder)
    {
        AppSettings.Instance.AddRecentOutput(folder);
        AppSettings.Instance.Save();
        RefreshHistoryDropdowns();

        OutputDirectoryBox.Text = folder;
    }

    // Repopulates both ComboBox ItemsSource lists from saved history.
    // Uses a suppress flag to prevent SelectionChanged firing during the update.
    private void RefreshHistoryDropdowns()
    {
        _suppressDirectorySelection = true;

        var currentInput  = InputDirectoryBox.Text;
        var currentOutput = OutputDirectoryBox.Text;

        InputDirectoryBox.ItemsSource  = AppSettings.Instance.RecentInputFolders.ToList();
        OutputDirectoryBox.ItemsSource = AppSettings.Instance.RecentOutputFolders.ToList();

        // Restore the displayed text — ItemsSource reset clears it
        InputDirectoryBox.Text  = currentInput;
        OutputDirectoryBox.Text = currentOutput;

        _suppressDirectorySelection = false;
    }

    private static string? BrowseFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    // ── Load movie folders into the left list ────────────────────────
    // Scans the input directory for subfolders, skipping "Remux" and
    // "Transcode" which are used for settings files, not source material.
    private void LoadMovieFolders(string rootPath)
    {
        FolderList.Items.Clear();

        try
        {
            var folders = Directory.GetDirectories(rootPath)
                .Select(Path.GetFileName)
                .Where(name => name != null &&
                               !name.Equals("Remux",     StringComparison.OrdinalIgnoreCase) &&
                               !name.Equals("Transcode", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name);

            foreach (var folderName in folders)
            {
                if (folderName == null) continue;

                var folderPath = Path.Combine(rootPath, folderName);

                // Detect whether this is a TV show (contains subfolders)
                // or a movie (contains .mkv files directly).
                // A folder with subfolders is treated as a TV show — its
                // subfolders are assumed to be seasons.
                var subFolders = Directory.GetDirectories(folderPath);

                if (subFolders.Length > 0)
                {
                    // ── TV show node ──────────────────────────────────
                    var showNode = new ShowNode { DisplayName = folderName };

                    foreach (var seasonPath in subFolders.OrderBy(s => s))
                    {
                        var seasonName = Path.GetFileName(seasonPath);
                        if (seasonName == null) continue;

                        showNode.Children.Add(new SeasonNode
                        {
                            DisplayName = seasonName,
                            // FolderPath is relative to input directory —
                            // "Show Name\Season 01" — used as _selectedMovieFolder
                            FolderPath  = Path.Combine(folderName, seasonName)
                        });
                    }

                    FolderList.Items.Add(showNode);
                }
                else
                {
                    // ── Movie folder node ─────────────────────────────
                    FolderList.Items.Add(new FolderNode
                    {
                        DisplayName = folderName,
                        FolderPath  = folderName
                    });
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read directory:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Folder selected → populate the TreeView ──────────────────────
    // Handles selection of both FolderNode (movie) and SeasonNode (TV season).
    // ShowNode clicks are ignored — user must select a season.
    private void FolderList_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        // Determine the folder path relative to input directory
        string? folderPath = e.NewValue switch
        {
            FolderNode f  => f.FolderPath,
            SeasonNode s  => s.FolderPath,
            _             => null   // ShowNode or anything else — ignore
        };

        if (folderPath == null) return;

        _selectedMovieFolder = folderPath;
        FileTree.Items.Clear();
        ClearTrackTables();
        HideSelectedFileBar();

        var fullFolderPath = Path.Combine(_inputDirectory, folderPath);

        try
        {
            // Get all .mkv files in the folder, sorted alphabetically
            var files = Directory.GetFiles(fullFolderPath, "*.mkv")
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .OrderBy(f => f)
                .ToList();

            // Determine the mode string for checking settings files
            var modeFolder = _isTranscodeMode ? "Transcode" : "Remux";

            foreach (var fileName in files)
            {
                if (fileName == null) continue;

                var nameNoExt = Path.GetFileNameWithoutExtension(fileName);

                // Determine the category by splitting on the LAST dash.
                var lastDash = nameNoExt.LastIndexOf('-');

                string? category = null;
                string displayName = nameNoExt;

                if (lastDash > 0)
                {
                    var suffix = nameNoExt.Substring(lastDash + 1);

                    if (KnownCategories.Contains(suffix))
                    {
                        category    = suffix.ToLowerInvariant();
                        displayName = nameNoExt.Substring(0, lastDash);
                    }
                }

                // Check if a settings file already exists for this file.
                // For TV: folderPath is "Show\Season 01" so the settings path
                // mirrors that structure under the Remux/Transcode folder.
                var settingsPath = Path.Combine(_inputDirectory, modeFolder,
                    folderPath, nameNoExt + ".txt");
                var hasSettings = File.Exists(settingsPath);

                var leaf = new FileLeafNode
                {
                    DisplayName = displayName,
                    FileName    = fileName,
                    GroupKey    = category,
                    Background  = hasSettings
                        ? System.Windows.Media.Brushes.Green
                        : System.Windows.Media.Brushes.Transparent
                };

                if (category == null)
                {
                    FileTree.Items.Insert(0, leaf);
                }
                else
                {
                    var group = FindOrCreateGroup(category);
                    group.Children.Add(leaf);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read folder:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Finds an existing group node in the TreeView by category key,
    // or creates a new one if it doesn't exist yet.
    // e.g. FindOrCreateGroup("deleted") returns the "Deleted" group node.
    private FileGroupNode FindOrCreateGroup(string categoryKey)
    {
        // Search existing top-level items for a matching group node
        foreach (var item in FileTree.Items)
        {
            // "is FileGroupNode g" — pattern match that also assigns to g
            // In VB.NET: If TypeOf item Is FileGroupNode Then Dim g = CType(item, FileGroupNode)
            if (item is FileGroupNode g && g.CategoryKey == categoryKey)
                return g;
        }

        // Not found — create it with a capitalised display name
        // e.g. "deleted" → "Deleted"
        // char.ToUpper converts first char, the rest stays the same via Substring(1)
        var displayName = char.ToUpper(categoryKey[0]) + categoryKey.Substring(1);

        var newGroup = new FileGroupNode
        {
            DisplayName = displayName,
            CategoryKey = categoryKey
        };

        FileTree.Items.Add(newGroup);
        return newGroup;
    }

    // ── File selected in TreeView → populate tracks ──────────────────
    // "async void" is the correct signature for WPF event handlers that
    // need to use await. Normally async methods return Task, but event
    // handlers must return void — this is the one accepted exception.
    // In VB.NET: Async Sub FileTree_SelectedItemChanged(...)
    private async void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // If a GROUP node was clicked, expand it and select its first child.
        // This way the user never needs to click twice — once to expand,
        // once to select a file.
        if (e.NewValue is FileGroupNode group)
        {
            if (group.Children.Count == 0) return;

            // Find the TreeViewItem container for this group node.
            // In WPF, the visual TreeViewItem is separate from the data object
            // (FileGroupNode). ItemContainerGenerator bridges the two.
            // In VB.NET terms: it's like finding the TreeNode that wraps our data object.
            var groupContainer = FileTree.ItemContainerGenerator
                .ContainerFromItem(group) as TreeViewItem;

            if (groupContainer == null) return;

            // Expand the group so its children are visible
            groupContainer.IsExpanded = true;

            // Now select the first child leaf.
            // We must call UpdateLayout() first because the child TreeViewItem
            // containers don't exist in the visual tree until after expansion
            // is rendered. Without this, ContainerFromItem returns null.
            groupContainer.UpdateLayout();

            var firstChild = group.Children[0];
            var childContainer = groupContainer.ItemContainerGenerator
                .ContainerFromItem(firstChild) as TreeViewItem;

            if (childContainer != null)
                childContainer.IsSelected = true;

            // The IsSelected = true above will fire SelectedItemChanged again
            // with the leaf node, which will populate the tracks.
            return;
        }

        // Only respond to leaf nodes (actual files)
        if (e.NewValue is not FileLeafNode leaf) return;

        // The leaf's Background is Green when a settings file exists (set during
        // tree population and after a successful save). Drive the Remux View Command
        // button enabled state from this — it should only be clickable when there
        // is actually a saved command to read.
        ViewRemuxCommandBtn.IsEnabled =
            leaf.Background == System.Windows.Media.Brushes.Green;

        var fullPath = Path.Combine(_inputDirectory, _selectedMovieFolder, leaf.FileName);
        ShowSelectedFileBar(Path.GetFileNameWithoutExtension(leaf.FileName));

        await PopulateTracksForFileAsync(fullPath);
    }

    // Shows the selected file bar with the given filename
    private void ShowSelectedFileBar(string fileNameNoExt)
    {
        SelectedFileLabel.Text    = $"{fileNameNoExt} Details:";
        SelectedFileBar.Visibility = Visibility.Visible;
    }

    private void HideSelectedFileBar()
    {
        SelectedFileBar.Visibility = Visibility.Collapsed;
        SelectedFileLabel.Text     = "";
    }

    // ── Track population via ffprobe ─────────────────────────────────
    // Calls FfprobeService to probe the file, then populates all six
    // track collections from the result. The "await" means the UI stays
    // responsive while ffprobe runs — no frozen window.
    //
    // "async Task" (not async void) because this is a helper method, not
    // an event handler directly. Only event handlers use async void.
    private async Task PopulateTracksForFileAsync(string fullPath)
    {
        ClearTrackTables();

        try
        {
            var result = await FfprobeService.ProbeFileAsync(fullPath);

            // Populate Remux tracks
            foreach (var t in result.RemuxVideo)    RemuxVideoTracks.Add(t);
            foreach (var t in result.RemuxAudio)
            {
                // Wire IsSelected changes to re-evaluate drag availability.
                // Drag reorder only makes sense when 2+ tracks are selected.
                t.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(RemuxAudioTrack.IsSelected))
                        UpdateAudioDragState();
                };
                RemuxAudioTracks.Add(t);
            }
            foreach (var t in result.RemuxSubtitle) RemuxSubtitleTracks.Add(t);

            // Populate Transcode tracks
            foreach (var t in result.TranscodeVideo)    TranscodeVideoTracks.Add(t);
            foreach (var t in result.TranscodeAudio)    TranscodeAudioTracks.Add(t);
            foreach (var t in result.TranscodeSubtitle) TranscodeSubtitleTracks.Add(t);

            // If a settings file exists for this file, load it to restore
            // previously saved track selections and order.
            if (FileTree.SelectedItem is FileLeafNode leaf)
                LoadSettingsForFile(leaf);

            // ── Auto-select single audio track ────────────────────────
            // If there is exactly one audio track and no settings were loaded
            // (i.e. the track is still unselected), select it automatically.
            // We only do this for a single track — more than one requires the
            // user to make an explicit choice to avoid unintended selections.
            if (RemuxAudioTracks.Count == 1 && !RemuxAudioTracks[0].IsSelected)
                RemuxAudioTracks[0].IsSelected = true;

            // Set initial drag state based on how many tracks are selected.
            // The PropertyChanged wires above keep this current as the user
            // checks and unchecks tracks.
            UpdateAudioDragState();
        }
        catch (InvalidOperationException)
        {
            MessageBox.Show(
                "There was an issue with FFprobe. Please check the path in Preferences.",
                "FFprobe Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not read track information:\n{ex.Message}",
                "FFprobe Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Load saved settings for a file ───────────────────────────────
    // If a settings .txt file exists for the selected file, reads it and
    // restores the previously saved track state.
    //
    // For Remux, the command contains:
    //   --audio-tracks 1,3,2    which tracks are selected AND their order
    //   --subtitle-tracks 30,31 which subtitle tracks are selected
    //
    // For Transcode, the command is a direct ffmpeg invocation:
    //   -c:v hevc_nvenc         video encode codec (after -i)
    //   -preset p5              encode preset
    //   -c:a:N copy/eac3/ac3   per-track audio codec
    //   -b:a:N 640k             per-track audio bitrate when encoding
    private void LoadSettingsForFile(FileLeafNode leaf)
    {
        var modeFolder   = _isTranscodeMode ? "Transcode" : "Remux";
        var nameNoExt    = Path.GetFileNameWithoutExtension(leaf.FileName);
        var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                               _selectedMovieFolder, nameNoExt + ".txt");

        if (!File.Exists(settingsFile)) return;

        var command = File.ReadAllText(settingsFile);

        if (_isTranscodeMode)
            LoadTranscodeSettings(command);
        else
            LoadRemuxSettings(command);
    }

    // ── Restore Remux track state from a saved command string ─────────
    // Parses --audio-tracks and --subtitle-tracks from the mkvmerge command,
    // restores IsSelected on each track, and re-applies any saved audio order.
    private void LoadRemuxSettings(string command)
    {
        // ── Parse --audio-tracks ──────────────────────────────────────
        // Extract the comma-separated index list from "--audio-tracks 1,3,2"
        var audioIndexes    = ParseTrackIndexes(command, "--audio-tracks");
        var subtitleIndexes = ParseTrackIndexes(command, "--subtitle-tracks");

        if (audioIndexes.Count == 0 && subtitleIndexes.Count == 0) return;

        // ── Apply audio selections and order ──────────────────────────
        if (audioIndexes.Count > 0)
        {
            // First mark IsSelected on all matching tracks
            foreach (var track in RemuxAudioTracks)
                track.IsSelected = audioIndexes.Contains(track.OriginalTrackIndex);

            // Only reorder if there are 2+ selected tracks AND they are not in
            // ascending order. A single selected track can never be reordered,
            // and IsAscendingOrder would return true trivially for one element
            // anyway — this guard makes that intent explicit.
            // e.g. "1,2,8" — tracks are in natural order, no reorder needed.
            // e.g. "2,1,8" — tracks 1 and 2 were swapped by the user, restore it.
            if (audioIndexes.Count >= 2 && !CommandBuilder.IsAscendingOrder(audioIndexes))
            {
                // Find the current collection indexes (slots) occupied by the
                // selected tracks, in ascending order.
                // e.g. for selected tracks 1, 2, 8 in a 9-track list:
                // slots = [0, 1, 7]
                var slots = audioIndexes
                    .Select(idx => RemuxAudioTracks.IndexOf(
                        RemuxAudioTracks.First(t => t.OriginalTrackIndex == idx)))
                    .OrderBy(i => i)
                    .ToList();

                // Build the desired track order from the saved sequence.
                // e.g. saved order "2,1,8" → [track2, track1, track8]
                var savedOrder = audioIndexes
                    .Select(idx => RemuxAudioTracks.First(t => t.OriginalTrackIndex == idx))
                    .ToList();

                // Place each track into its assigned slot.
                // Track 2 goes to slot 0, track 1 to slot 1, track 8 to slot 7.
                // Unselected tracks (slots not in our list) are never touched.
                for (int i = 0; i < slots.Count; i++)
                {
                    var targetSlot  = slots[i];
                    var currentSlot = RemuxAudioTracks.IndexOf(savedOrder[i]);
                    if (currentSlot != targetSlot)
                        RemuxAudioTracks.Move(currentSlot, targetSlot);
                }
            }
        }

        // ── Apply subtitle selections ─────────────────────────────────
        // Subtitles don't support drag reorder so we only set IsSelected.
        if (subtitleIndexes.Count > 0)
        {
            foreach (var track in RemuxSubtitleTracks)
                track.IsSelected = subtitleIndexes.Contains(track.OriginalTrackIndex);
        }
    }

    // ── Restore Transcode track state from a saved ffmpeg command ─────
    // Parses the ffmpeg command and restores:
    //   - Video OutputFormat  (-c:v after -i: hevc_nvenc → "hevc (default)", else "h.264")
    //   - Video Preset        (-preset pN)
    //   - Audio Format        (-c:a:N copy → "Keep", eac3/ac3 → that value)
    //   - Audio BitRate       (-b:a:N value, stripped of 'k' suffix)
    private void LoadTranscodeSettings(string command)
    {
        var tokens = command.Split(' ');

        // ── Video: OutputFormat and Preset ────────────────────────────
        // The command has two -c:v tokens: one before -i (the hardware decoder)
        // and one after -i (the encoder). We find -i first then search after it.
        var video = TranscodeVideoTracks.FirstOrDefault();
        if (video != null)
        {
            var inputIndex = Array.IndexOf(tokens, "-i");
            if (inputIndex >= 0)
            {
                for (int i = inputIndex + 1; i < tokens.Length - 1; i++)
                {
                    if (tokens[i] == "-c:v")
                    {
                        video.OutputFormat = tokens[i + 1].Equals(
                            "hevc_nvenc", StringComparison.OrdinalIgnoreCase)
                            ? "hevc (default)"
                            : "h.264";
                        break;
                    }
                }
            }

            var presetIndex = Array.IndexOf(tokens, "-preset");
            if (presetIndex >= 0 && presetIndex + 1 < tokens.Length)
                video.Preset = tokens[presetIndex + 1];
        }

        // ── Audio: Format and BitRate ─────────────────────────────────
        // Scan for -c:a:N <codec> and -b:a:N <value> tokens.
        // N is the 0-based audio index matching the collection order.
        var audioFormats  = new Dictionary<int, string>();
        var audioBitrates = new Dictionary<int, string>();

        for (int i = 0; i < tokens.Length - 1; i++)
        {
            // -c:a:N <codec>
            if (tokens[i].StartsWith("-c:a:") &&
                int.TryParse(tokens[i].Substring(5), out var caIdx))
            {
                audioFormats[caIdx] = tokens[i + 1].Equals(
                    "copy", StringComparison.OrdinalIgnoreCase)
                    ? "Keep"
                    : tokens[i + 1];
            }

            // -b:a:N <value>  e.g. "640k"
            if (tokens[i].StartsWith("-b:a:") &&
                int.TryParse(tokens[i].Substring(5), out var baIdx))
            {
                audioBitrates[baIdx] = tokens[i + 1].TrimEnd('k', 'K');
            }
        }

        for (int i = 0; i < TranscodeAudioTracks.Count; i++)
        {
            if (audioFormats.TryGetValue(i, out var fmt))
                TranscodeAudioTracks[i].Format = fmt;

            if (audioBitrates.TryGetValue(i, out var br))
                TranscodeAudioTracks[i].BitRate = br;
        }
    }

    // Parses a track index list from a command string.
    // e.g. given "--audio-tracks 1,3,2" returns [1, 3, 2]
    // Returns an empty list if the flag is not found.
    private static List<int> ParseTrackIndexes(string command, string flag)
    {
        var result = new List<int>();

        var pos = command.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return result;

        // Move past the flag name to the value
        var valueStart = pos + flag.Length;

        // Skip any whitespace between the flag and the value
        while (valueStart < command.Length && command[valueStart] == ' ')
            valueStart++;

        // Read characters until we hit a space or end of string
        var valueEnd = valueStart;
        while (valueEnd < command.Length && command[valueEnd] != ' ')
            valueEnd++;

        var value = command.Substring(valueStart, valueEnd - valueStart);

        foreach (var part in value.Split(','))
        {
            if (int.TryParse(part.Trim(), out var idx))
                result.Add(idx);
        }

        return result;
    }

    private void ClearTrackTables()
    {
        RemuxVideoTracks.Clear();
        RemuxAudioTracks.Clear();
        RemuxSubtitleTracks.Clear();
        TranscodeVideoTracks.Clear();
        TranscodeAudioTracks.Clear();
        TranscodeSubtitleTracks.Clear();

        // Disable drag until tracks are loaded and 2+ are selected.
        if (RemuxAudioList != null)
            RemuxAudioList.AllowDrop = false;
    }

    // ── Audio drag state ─────────────────────────────────────────────
    // Drag reorder only makes sense when 2 or more audio tracks are selected.
    // Called after track population, after settings load, and whenever
    // IsSelected changes on any RemuxAudioTrack.
    private void UpdateAudioDragState()
    {
        var selectedCount = RemuxAudioTracks.Count(t => t.IsSelected);
        RemuxAudioList.AllowDrop = selectedCount >= 2;
    }

    // These three handlers work together:
    // 1. PreviewMouseLeftButtonDown — record which row the drag started on
    // 2. PreviewMouseMove — once mouse moves far enough, start the drag
    // 3. Drop — reorder the collection when the row is dropped

    private void AudioList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => StartDragCapture(RemuxAudioList, e);

    private void AudioList_PreviewMouseMove(object sender, MouseEventArgs e)
        => HandleDragMove(RemuxAudioList, e);

    private void AudioList_Drop(object sender, DragEventArgs e)
        => HandleDrop(RemuxAudioList, RemuxAudioTracks, e);

    private void AudioList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => ToggleRowSelection<RemuxAudioTrack>(RemuxAudioList, e);

    // ── Drag and drop for Subtitle ListView ─────────────────────────
    private void SubtitleList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => StartDragCapture(RemuxSubtitleList, e);

    private void SubtitleList_PreviewMouseMove(object sender, MouseEventArgs e)
        => HandleDragMove(RemuxSubtitleList, e);

    private void SubtitleList_Drop(object sender, DragEventArgs e)
        => HandleDrop(RemuxSubtitleList, RemuxSubtitleTracks, e);

    private void SubtitleList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => ToggleRowSelection<RemuxSubtitleTrack>(RemuxSubtitleList, e);

    // ── Drag and drop implementation ─────────────────────────────────

    // Toggles IsSelected on a track when the user clicks a row.
    // Uses reflection to access IsSelected since it is defined on each
    // subclass rather than the shared ObservableBase.
    // Only fires if _dragFromIndex is still set (i.e. no drag occurred —
    // HandleDrop resets it to -1 after a completed drag).
    private void ToggleRowSelection<T>(ListView list, MouseButtonEventArgs e) where T : class
    {
        var hit  = list.InputHitTest(e.GetPosition(list)) as DependencyObject;
        var item = FindAncestor<ListViewItem>(hit);
        if (item?.DataContext is not T track) return;

        // _dragFromIndex >= 0 means StartDragCapture fired but no drag occurred.
        // Reset it and toggle IsSelected.
        if (_dragFromIndex < 0) return;
        _dragFromIndex = -1;

        var prop = typeof(T).GetProperty("IsSelected");
        if (prop == null) return;
        var current = (bool)(prop.GetValue(track) ?? false);
        prop.SetValue(track, !current);
    }

    // Records the row index that the mouse button went down on.
    // Uses HitTest to find which item is under the mouse pointer.
    private void StartDragCapture(ListView list, MouseButtonEventArgs e)
    {
        // If drag is disabled for this list (e.g. fewer than 2 audio tracks
        // selected), don't capture — let normal click handling proceed.
        if (!list.AllowDrop) return;

        // VisualTreeHelper.HitTest finds whatever visual element is under
        // the mouse. We walk up the visual tree to find the ListViewItem.
        var hit = list.InputHitTest(e.GetPosition(list)) as DependencyObject;

        // If the click landed on a CheckBox, do not start drag capture.
        // This lets the CheckBox handle its own click and toggle normally.
        // Without this check, the drag handler captures the mouse first and
        // the CheckBox never sees the click.
        if (FindAncestor<CheckBox>(hit) != null) return;

        var item = FindAncestor<ListViewItem>(hit);
        if (item == null) return;

        _dragFromIndex  = list.Items.IndexOf(item.DataContext);
        _dragSourceList = list;
    }

    // Starts the actual drag operation once the mouse has moved more than
    // the system-defined minimum drag distance.
    private void HandleDragMove(ListView list, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragFromIndex < 0 || _dragSourceList != list) return;

        // SystemParameters.MinimumHorizontalDragDistance / MinimumVerticalDragDistance
        // are the minimum distances the mouse must move before a drag is initiated.
        // This prevents accidental drags when the user just clicks.
        var pos = e.GetPosition(list);
        if (Math.Abs(pos.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // DoDragDrop initiates the drag. We pass the index as the data payload.
        // DragDropEffects.Move tells Windows this is a move, not a copy.
        DragDrop.DoDragDrop(list, _dragFromIndex, DragDropEffects.Move);
    }

    // Handles the drop by moving the item in the underlying collection.
    // Because the collection is an ObservableCollection, the ListView
    // automatically updates to reflect the new order.
    //
    // The generic <T> parameter means this one method works for both
    // audio tracks and subtitle tracks without duplicating code.
    // In VB.NET you would need a separate Sub for each type.
    private void HandleDrop<T>(ListView list, ObservableCollection<T> collection, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(int))) return;

        var fromIndex = (int)e.Data.GetData(typeof(int));

        // Find which row the item was dropped onto using HitTest
        var hit  = list.InputHitTest(e.GetPosition(list)) as DependencyObject;
        var item = FindAncestor<ListViewItem>(hit);

        int toIndex;
        if (item != null)
        {
            toIndex = list.Items.IndexOf(item.DataContext);
        }
        else
        {
            // Drop landed outside the list bounds (above or below all rows).
            // Get the drop Y position relative to the list.
            var dropY = e.GetPosition(list).Y;

            // If above the top of the list, move to first position.
            // If below the last row (or anywhere else out of bounds), move to last.
            toIndex = dropY < 0 ? 0 : collection.Count - 1;
        }

        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return;

        // Move the item in the collection.
        // ObservableCollection.Move(oldIndex, newIndex) does exactly what we need.
        // The UI updates automatically because ObservableCollection fires
        // a CollectionChanged event that the ListView is bound to.
        collection.Move(fromIndex, toIndex);

        _dragFromIndex  = -1;
        _dragSourceList = null;
    }

    // Walks up the WPF visual tree to find the nearest ancestor of type T.
    // Used to find the ListViewItem that contains the element the mouse hit.
    // "where T : DependencyObject" constrains T to WPF visual elements.
    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T target) return target;
            // VisualTreeHelper.GetParent walks one level up the visual tree
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    // ── Bottom button handlers ───────────────────────────────────────
    private void OpenInMPV_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedFilePath();
        if (path == null) return;
        TryLaunch(AppSettings.Instance.MPV_Path, path);
    }

    private void OpenInSubtitleEdit_Click(object sender, RoutedEventArgs e)
    {
        var path = GetSelectedFilePath();
        if (path == null) return;
        TryLaunch(AppSettings.Instance.SubtitleEdit_Path, path);
    }

    private void OpenFolderInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedMovieFolder)) return;
        var folderPath = Path.Combine(_inputDirectory, _selectedMovieFolder);
        System.Diagnostics.Process.Start("explorer.exe", folderPath);
    }

    // ── Right-click rename ───────────────────────────────────────────
    // Fired from the context menu on a FileLeafNode in the right-panel TreeView.
    // The ContextMenu is attached to the TextBlock inside the DataTemplate so we
    // retrieve the leaf via the sender's DataContext rather than FileTree.SelectedItem.
    private void RenameLeaf_Click(object sender, RoutedEventArgs e)
    {
        // Retrieve the leaf from the MenuItem's DataContext, walking up through
        // the ContextMenu to the TextBlock it was attached to.
        if (sender is not MenuItem menuItem) return;
        if (menuItem.DataContext is not FileLeafNode leaf)
        {
            // ContextMenu.PlacementTarget is the TextBlock (a FrameworkElement);
            // cast to FrameworkElement first to access DataContext.
            if (menuItem.Parent is ContextMenu cm &&
                cm.PlacementTarget is FrameworkElement fe &&
                fe.DataContext is FileLeafNode placementLeaf)
                leaf = placementLeaf;
            else
                return;
        }

        if (string.IsNullOrWhiteSpace(_inputDirectory) ||
            string.IsNullOrWhiteSpace(_selectedMovieFolder)) return;

        var oldFileName   = leaf.FileName;
        var oldNameNoExt  = Path.GetFileNameWithoutExtension(oldFileName);

        // ── Show rename dialog ────────────────────────────────────────
        var newNameNoExt = ShowRenameDialog(oldFileName);
        if (newNameNoExt == null) return; // user cancelled

        // Enforce .mkv extension: if the user's input already ends with .mkv
        // (case-insensitive) keep it, otherwise append it.
        var newFileName = newNameNoExt.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            ? newNameNoExt
            : newNameNoExt + ".mkv";

        // No-op if the name hasn't changed
        if (newFileName.Equals(oldFileName, StringComparison.OrdinalIgnoreCase)) return;

        var folderPath   = Path.Combine(_inputDirectory, _selectedMovieFolder);
        var oldFilePath  = Path.Combine(folderPath, oldFileName);
        var newFilePath  = Path.Combine(folderPath, newFileName);

        // ── Validate ──────────────────────────────────────────────────
        if (!File.Exists(oldFilePath))
        {
            MessageBox.Show("The original file could not be found on disk.",
                "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (File.Exists(newFilePath))
        {
            MessageBox.Show($"A file named \"{newFileName}\" already exists in this folder.",
                "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Rename the .mkv file ──────────────────────────────────────
        try
        {
            File.Move(oldFilePath, newFilePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not rename file:\n{ex.Message}",
                "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // ── Rename matching .txt settings files ───────────────────────
        // Check both Remux and Transcode settings folders regardless of
        // current mode — the file may have settings saved in both.
        var oldTxtName = oldNameNoExt + ".txt";
        var newTxtName = Path.GetFileNameWithoutExtension(newFileName) + ".txt";

        foreach (var modeFolder in new[] { "Remux", "Transcode" })
        {
            var settingsDir = Path.Combine(_inputDirectory, modeFolder, _selectedMovieFolder);
            var oldTxtPath  = Path.Combine(settingsDir, oldTxtName);
            var newTxtPath  = Path.Combine(settingsDir, newTxtName);

            if (File.Exists(oldTxtPath))
            {
                try
                {
                    File.Move(oldTxtPath, newTxtPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"File renamed but could not rename {modeFolder} settings file:\n{ex.Message}",
                        "Settings Rename Warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        // ── Update the tree node ──────────────────────────────────────
        // Derive the new display name using the same logic as folder load:
        // split on the last dash, check if the suffix is a known category.
        leaf.FileName = newFileName;
        var newNameNoExtFinal = Path.GetFileNameWithoutExtension(newFileName);
        var lastDash  = newNameNoExtFinal.LastIndexOf('-');
        if (lastDash > 0)
        {
            var suffix = newNameNoExtFinal.Substring(lastDash + 1);
            leaf.DisplayName = KnownCategories.Contains(suffix)
                ? newNameNoExtFinal.Substring(0, lastDash)
                : newNameNoExtFinal;
        }
        else
        {
            leaf.DisplayName = newNameNoExtFinal;
        }

        // Update the selected file bar if this file is currently selected
        if (FileTree.SelectedItem == leaf)
            ShowSelectedFileBar(Path.GetFileNameWithoutExtension(newFileName));
    }

    // Shows a simple rename dialog pre-populated with the current filename
    // (without extension). Returns the new name entered by the user, or null
    // if they cancelled. Does not validate or modify the name.
    private string? ShowRenameDialog(string currentFileName)
    {
        var win = new Window
        {
            Title                 = "Rename File",
            Width                 = 500,
            Height                = 140,
            MinWidth              = 350,
            MinHeight             = 140,
            MaxHeight             = 140,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = (System.Windows.Media.Brush)FindResource("WindowBg"),
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false
        };

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var textBox = new TextBox
        {
            Text              = currentFileName,
            FontSize          = 13,
            Foreground        = (System.Windows.Media.Brush)FindResource("ForegroundColor"),
            Background        = (System.Windows.Media.Brush)FindResource("InputBg"),
            BorderBrush       = (System.Windows.Media.Brush)FindResource("BorderColor"),
            BorderThickness   = new Thickness(1),
            Padding           = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(textBox, 0);

        var buttonPanel = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width   = 80,
            Margin  = new Thickness(0, 0, 8, 0),
            Style   = (Style)FindResource("FlatButton")
        };

        var okBtn = new Button
        {
            Content = "OK",
            Width   = 80,
            Style   = (Style)FindResource("AccentButton"),
            IsDefault = true
        };

        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(okBtn);
        Grid.SetRow(buttonPanel, 2);

        grid.Children.Add(textBox);
        grid.Children.Add(buttonPanel);
        win.Content = grid;

        string? result = null;

        okBtn.Click += (_, _) =>
        {
            var input = textBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show("Please enter a filename.", "Rename",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            result = input;
            win.DialogResult = true;
            win.Close();
        };

        cancelBtn.Click += (_, _) =>
        {
            win.DialogResult = false;
            win.Close();
        };

        // Focus the text box and position caret at the end on load
        win.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.CaretIndex = textBox.Text.Length;
        };

        win.ShowDialog();
        return result;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        // ── Validate prerequisites ────────────────────────────────────
        // Must have a file selected, an output directory, and an input directory.
        if (FileTree.SelectedItem is not FileLeafNode leaf)
        {
            MessageBox.Show("No file selected. Please select a file from the tree.",
                "Save Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
        {
            MessageBox.Show("No output directory chosen. Save cancelled.",
                "Save Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Build the command string ──────────────────────────────────
        string command;
        try
        {
            if (_isTranscodeMode)
            {
                command = CommandBuilder.BuildTranscodeCommand(
                    OutputDirectoryBox.Text,
                    _selectedMovieFolder,
                    leaf.FileName,
                    _inputDirectory,
                    TranscodeVideoTracks,
                    TranscodeAudioTracks,
                    TranscodeSubtitleTracks);
            }
            else
            {
                command = CommandBuilder.BuildRemuxCommand(
                    OutputDirectoryBox.Text,
                    _selectedMovieFolder,
                    leaf.FileName,
                    _inputDirectory,
                    RemuxAudioTracks,
                    RemuxSubtitleTracks);
            }
        }
        catch (InvalidOperationException ex)
        {
            // e.g. no audio tracks selected
            MessageBox.Show(ex.Message, "Save Cancelled",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Build the settings file path ──────────────────────────────
        // Mirrors the input folder structure under a "Remux" or "Transcode"
        // subfolder in the input directory.
        // e.g. H:\Video\Remux\Casino Royale (2006)\Casino Royale (2006).txt
        //
        // For extras (leaf nodes with a GroupKey), the filename includes the
        // category suffix, matching the original app's behaviour.
        // e.g. "Gettler Raises Bond's Suspicions-deleted.txt"
        var modeFolder   = _isTranscodeMode ? "Transcode" : "Remux";
        var nameNoExt    = Path.GetFileNameWithoutExtension(leaf.FileName);
        var settingsDir  = Path.Combine(_inputDirectory, modeFolder, _selectedMovieFolder);
        var settingsFile = Path.Combine(settingsDir, nameNoExt + ".txt");

        // ── Confirm overwrite if file already exists ──────────────────
        if (File.Exists(settingsFile))
        {
            var result = MessageBox.Show(
                $"{modeFolder} settings already exist. Overwrite?",
                $"{modeFolder} Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;
        }

        // ── Write the file ────────────────────────────────────────────
        try
        {
            // Create the folder structure if it doesn't exist yet.
            // CreateDirectory does nothing if the folder already exists.
            Directory.CreateDirectory(settingsDir);

            // Write the command as UTF-8 text.
            // File.WriteAllText handles creating or overwriting the file.
            File.WriteAllText(settingsFile, command, System.Text.Encoding.UTF8);

            // Update the TreeView node to green to show settings exist.
            // This matches the original app's visual feedback.
            leaf.Background = System.Windows.Media.Brushes.Green;

            // A settings file now exists — enable the Remux View Command button.
            if (!_isTranscodeMode)
                ViewRemuxCommandBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save settings file:\n{ex.Message}",
                "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── View Command ──────────────────────────────────────────────────
    // Remux: reads the saved .txt settings file (button is disabled when
    //        no file exists, so we can assume it's there).
    // Transcode: reads the saved .txt file if one exists, otherwise
    //            builds the command from current UI state.
    private void ViewCommand_Click(object sender, RoutedEventArgs e)
    {
        if (FileTree.SelectedItem is not FileLeafNode leaf) return;

        var modeFolder   = _isTranscodeMode ? "Transcode" : "Remux";
        var nameNoExt    = Path.GetFileNameWithoutExtension(leaf.FileName);
        var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                               _selectedMovieFolder, nameNoExt + ".txt");

        string command;

        if (File.Exists(settingsFile))
        {
            // Ground truth — read exactly what will be executed.
            command = File.ReadAllText(settingsFile, System.Text.Encoding.UTF8);
        }
        else
        {
            // Transcode only: no settings file yet, build from current UI state.
            if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
            {
                MessageBox.Show("No output directory chosen.",
                    "View Command", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                command = CommandBuilder.BuildTranscodeCommand(
                    OutputDirectoryBox.Text, _selectedMovieFolder, leaf.FileName,
                    _inputDirectory, TranscodeVideoTracks, TranscodeAudioTracks,
                    TranscodeSubtitleTracks);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(ex.Message, "View Command",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        ShowCommandPreview(command, leaf.FileName);
    }

    // Opens a small owned window showing the full command string in a
    // selectable, word-wrapped TextBox with a Copy button.
    private void ShowCommandPreview(string command, string fileName)
    {
        // ── Window shell ──────────────────────────────────────────────
        var win = new Window
        {
            Title           = $"Command — {fileName}",
            Width           = 800,
            Height          = 300,
            MinWidth        = 400,
            MinHeight       = 160,
            Owner           = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background      = (System.Windows.Media.Brush)
                                  FindResource("WindowBg"),
            ResizeMode      = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar   = false
        };

        // ── Layout ────────────────────────────────────────────────────
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Command TextBox ───────────────────────────────────────────
        // IsReadOnly keeps the text unchanged; IsReadOnlyCaretVisible + PART_ContentHost
        // together ensure the caret appears and text remains selectable/copyable.
        var textBox = new TextBox
        {
            Text              = command,
            IsReadOnly        = true,
            TextWrapping      = TextWrapping.Wrap,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
            FontSize          = 12,
            Foreground        = (System.Windows.Media.Brush)FindResource("ForegroundColor"),
            Background        = (System.Windows.Media.Brush)FindResource("SurfaceBg"),
            BorderBrush       = (System.Windows.Media.Brush)FindResource("BorderColor"),
            BorderThickness   = new Thickness(1),
            Padding           = new Thickness(8),
            IsReadOnlyCaretVisible = true
        };
        Grid.SetRow(textBox, 0);

        // ── Copy button ───────────────────────────────────────────────
        var copyBtn = new Button
        {
            Content             = "Copy to Clipboard",
            HorizontalAlignment = HorizontalAlignment.Right,
            Style               = (Style)FindResource("AccentButton")
        };
        copyBtn.Click += (_, _) =>
        {
            Clipboard.SetText(command);
            copyBtn.Content = "Copied!";
        };
        Grid.SetRow(copyBtn, 2);

        grid.Children.Add(textBox);
        grid.Children.Add(copyBtn);
        win.Content = grid;

        // Select all text immediately so the user can Ctrl+C without clicking
        win.Loaded += (_, _) => textBox.SelectAll();

        win.ShowDialog();
    }

    // Returns the full path to the currently selected file in the TreeView,
    // or null if nothing is selected.
    private string? GetSelectedFilePath()
    {
        if (FileTree.SelectedItem is not FileLeafNode leaf) return null;
        if (string.IsNullOrEmpty(_inputDirectory) || string.IsNullOrEmpty(_selectedMovieFolder))
            return null;
        return Path.Combine(_inputDirectory, _selectedMovieFolder, leaf.FileName);
    }

    // ── Preferences / startup ────────────────────────────────────────
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

    private void OpenPreferences()
    {
        var prefs = new UserPreferences { Owner = this };
        prefs.ShowDialog();
    }

    // ── Menu handlers ────────────────────────────────────────────────
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Transcode Tools\nModern WPF Edition", "About",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Preferences_Click(object sender, RoutedEventArgs e) => OpenPreferences();

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_inputDirectory))
        {
            MessageBox.Show("Please select an input directory first.",
                "No Input Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
        {
            MessageBox.Show("Please select an output directory first.",
                "No Output Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var runWindow = new RunRemux(_inputDirectory, OutputDirectoryBox.Text, _isTranscodeMode);
        runWindow.Owner = this;
        runWindow.ShowDialog();
    }

    // ── Helpers ──────────────────────────────────────────────────────
    private static void TryLaunch(string exe, string arg)
    {
        try
        {
            System.Diagnostics.Process.Start(exe, $"\"{arg}\"");
        }
        catch
        {
            MessageBox.Show($"Could not launch {exe}.\nCheck the path in Preferences.",
                "Launch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
