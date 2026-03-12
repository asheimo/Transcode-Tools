// ============================================================
// MainWindow.xaml.cs
// ============================================================

using System.Collections.ObjectModel;
using System.IO;
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

        SaveRemuxBtn.Visibility     = _isTranscodeMode ? Visibility.Collapsed : Visibility.Visible;
        SaveTranscodeBtn.Visibility = _isTranscodeMode ? Visibility.Visible   : Visibility.Collapsed;

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

        _inputDirectory = path;
        InputDirectoryBox.Text = path;
        LoadMovieFolders(path);

        // Clear the right panel and tracks when a new input is chosen
        FileTree.Items.Clear();
        ClearTrackTables();
        HideSelectedFileBar();
    }

    private void OpenOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFolder("Select Output Directory");
        if (path == null) return;
        OutputDirectoryBox.Text = path;
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

            foreach (var folder in folders)
                FolderList.Items.Add(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read directory:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Folder selected → populate the TreeView ──────────────────────
    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is not string folderName) return;

        _selectedMovieFolder = folderName;
        FileTree.Items.Clear();
        ClearTrackTables();
        HideSelectedFileBar();

        var folderPath = Path.Combine(_inputDirectory, folderName);

        try
        {
            // Get all .mkv files in the folder, sorted alphabetically
            var files = Directory.GetFiles(folderPath, "*.mkv")
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .OrderBy(f => f)
                .ToList();

            // Determine the mode string for checking settings files
            var modeFolder = _isTranscodeMode ? "Transcode" : "Remux";

            foreach (var fileName in files)
            {
                if (fileName == null) continue;

                // Strip the .mkv extension for display and analysis
                // Path.GetFileNameWithoutExtension handles this cleanly
                var nameNoExt = Path.GetFileNameWithoutExtension(fileName);

                // Determine the category by splitting on the LAST dash.
                // This fixes the original bug — "Ian Fleming - Secret Road-featurette"
                // was broken because the old code split on the first dash.
                //
                // LastIndexOf returns the position of the last '-' character.
                // If no dash exists, it returns -1.
                var lastDash = nameNoExt.LastIndexOf('-');

                string? category = null;
                string displayName = nameNoExt;

                if (lastDash > 0)
                {
                    // Substring(lastDash + 1) gets everything after the last dash
                    var suffix = nameNoExt.Substring(lastDash + 1);

                    if (KnownCategories.Contains(suffix))
                    {
                        // It's an extra — suffix is the category, display name is everything before the dash
                        category    = suffix.ToLowerInvariant();
                        displayName = nameNoExt.Substring(0, lastDash);
                    }
                    // If suffix is not a known category, treat the whole name as the display name
                    // (e.g. "Ian Fleming - Secret Road To Paradise" with no type suffix)
                }

                // Check if a settings file already exists for this file
                var settingsPath = Path.Combine(_inputDirectory, modeFolder, folderName,
                    nameNoExt + ".txt");
                var hasSettings = File.Exists(settingsPath);

                var leaf = new FileLeafNode
                {
                    DisplayName = displayName,
                    FileName    = fileName,
                    GroupKey    = category,
                    // Green background if settings exist, transparent if not
                    Background  = hasSettings
                        ? System.Windows.Media.Brushes.Green
                        : System.Windows.Media.Brushes.Transparent
                };

                if (category == null)
                {
                    // Root-level item — plain movie file, no category
                    // Insert at position 0 so the main movie appears at the top,
                    // same behaviour as the original app
                    FileTree.Items.Insert(0, leaf);
                }
                else
                {
                    // Find or create the group node for this category
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
    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
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

        var fullPath = Path.Combine(_inputDirectory, _selectedMovieFolder, leaf.FileName);
        ShowSelectedFileBar(Path.GetFileNameWithoutExtension(leaf.FileName));

        // TODO (Step 4): Replace this with a real ffprobe call
        PopulateTracksForFile(fullPath);
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

    // ── Track population (placeholder until Step 4 ffprobe) ─────────
    private void PopulateTracksForFile(string fullPath)
    {
        ClearTrackTables();

        // Placeholder data — will be replaced with real ffprobe output in Step 4
        RemuxVideoTracks.Add(new RemuxVideoTrack
        {
            OriginalTrackIndex = 0,
            VideoFormat = "h264",
            Resolution  = "1080p",
            Fps         = "24000/1001",
            IsDefault   = true
        });

        RemuxAudioTracks.Add(new RemuxAudioTrack
        {
            OriginalTrackIndex = 1,
            AudioFormat = "dts (DTS-HD MA)",
            Width       = "5.1(side)",
            BitRate     = "3886",
            Language    = "eng",
            Title       = "Surround 5.1",
            IsDefault   = true,
            IsSelected  = true
        });

        RemuxSubtitleTracks.Add(new RemuxSubtitleTrack
        {
            OriginalTrackIndex = 2,
            SubtitleFormat = "hdmv_pgs_subtitle",
            Language       = "eng",
            FrameCount     = "1840",
            IsSelected     = true
        });

        RemuxSubtitleTracks.Add(new RemuxSubtitleTrack
        {
            OriginalTrackIndex = 3,
            SubtitleFormat = "hdmv_pgs_subtitle",
            Language       = "fra",
            FrameCount     = "10",
            IsDefault      = true,
            IsSelected     = true
        });

        TranscodeVideoTracks.Add(new TranscodeVideoTrack
        {
            OriginalTrackIndex = 0,
            TrackInfo    = "Video: h264 1080p @ 24000/1001",
            Resolution   = "1080p",
            OutputFormat = "hevc",
            FrameRate    = "24000/1001"
        });

        TranscodeAudioTracks.Add(new TranscodeAudioTrack
        {
            OriginalTrackIndex = 1,
            TrackInfo = "Audio: dts (DTS-HD MA) 5.1 3886Kbps [eng]",
            Format    = "dts",
            Width     = "5.1(side)",
            BitRate   = "3886"
        });

        TranscodeSubtitleTracks.Add(new TranscodeSubtitleTrack
        {
            OriginalTrackIndex = 2,
            TrackInfo = "Subtitle: hdmv_pgs_subtitle [eng]"
        });
    }

    private void ClearTrackTables()
    {
        RemuxVideoTracks.Clear();
        RemuxAudioTracks.Clear();
        RemuxSubtitleTracks.Clear();
        TranscodeVideoTracks.Clear();
        TranscodeAudioTracks.Clear();
        TranscodeSubtitleTracks.Clear();
    }

    // ── Drag and drop for Audio ListView ────────────────────────────
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

    // ── Drag and drop for Subtitle ListView ─────────────────────────
    private void SubtitleList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => StartDragCapture(RemuxSubtitleList, e);

    private void SubtitleList_PreviewMouseMove(object sender, MouseEventArgs e)
        => HandleDragMove(RemuxSubtitleList, e);

    private void SubtitleList_Drop(object sender, DragEventArgs e)
        => HandleDrop(RemuxSubtitleList, RemuxSubtitleTracks, e);

    // ── Drag and drop implementation ─────────────────────────────────

    // Records the row index that the mouse button went down on.
    // Uses HitTest to find which item is under the mouse pointer.
    private void StartDragCapture(ListView list, MouseButtonEventArgs e)
    {
        // VisualTreeHelper.HitTest finds whatever visual element is under
        // the mouse. We walk up the visual tree to find the ListViewItem.
        var hit = list.InputHitTest(e.GetPosition(list)) as DependencyObject;
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
            // Dropped below all rows — move to end of list
            toIndex = collection.Count - 1;
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

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        // TODO (Step 5): Implement real settings save logic
        MessageBox.Show(
            _isTranscodeMode ? "Transcode settings saved." : "Remux settings saved.",
            "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
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
