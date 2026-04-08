// ============================================================
// MainWindow.xaml.cs
// ============================================================

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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
        // Pass the current folder so RefreshHistoryDropdowns sets Text
        // inside the suppress block, preventing a second SelectionChanged.
        RefreshHistoryDropdowns(inputText: folder);
        LoadMovieFolders(folder);

        FileTree.Items.Clear();
        ClearTrackTables();
        HideSelectedFileBar();
    }

    private void LoadOutputDirectory(string folder)
    {
        AppSettings.Instance.AddRecentOutput(folder);
        AppSettings.Instance.Save();
        RefreshHistoryDropdowns(outputText: folder);
    }

    // Repopulates both ComboBox ItemsSource lists from saved history.
    // Uses a suppress flag to prevent SelectionChanged firing during the update.
    // inputText/outputText: when provided, sets the ComboBox text inside the
    // suppress block so the assignment never fires SelectionChanged outside it.
    private void RefreshHistoryDropdowns(string? inputText = null, string? outputText = null)
    {
        _suppressDirectorySelection = true;

        var currentInput  = inputText  ?? InputDirectoryBox.Text;
        var currentOutput = outputText ?? OutputDirectoryBox.Text;

        InputDirectoryBox.ItemsSource  = AppSettings.Instance.RecentInputFolders.ToList();
        OutputDirectoryBox.ItemsSource = AppSettings.Instance.RecentOutputFolders.ToList();

        // Set text inside the suppress block. We do NOT clear SelectedItem here
        // as that also clears Text on an editable ComboBox. Instead, setting
        // Text directly is sufficient — the suppress flag prevents SelectionChanged
        // from firing while we are inside this method.
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
    // Regex to detect a (YYYY) year pattern anywhere in a folder name.
    private static readonly Regex YearPattern = new(@"\(\d{4}\)", RegexOptions.Compiled);

    // Regex to detect an existing resolution suffix e.g. -1080p, -2160p, -480p, -720p, -4K
    private static readonly Regex ResolutionSuffix =
        new(@"-(4K|2160p|1080p|720p|480p|576p)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void LoadMovieFolders(string rootPath)
    {
        FolderList.Items.Clear();

        // Collect movie FolderNodes separately so we can run corrections
        // after the tree is built. TV show nodes are added directly.
        var movieNodes = new List<FolderNode>();

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
                    var node = new FolderNode
                    {
                        DisplayName    = folderName,
                        FolderPath     = folderName,
                        HasYearWarning = !YearPattern.IsMatch(folderName)
                    };
                    FolderList.Items.Add(node);
                    movieNodes.Add(node);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read directory:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Load-time order: title case first, resolution append second ──
        // Title case runs first so the resolution suffix (e.g. -1080p) is
        // never passed through ToTitleCase and mangled to -1080P.
        if (AppSettings.Instance.TitleCaseEnabled && movieNodes.Count > 0)
            ApplyTitleCaseCorrections(rootPath, movieNodes);

        if (AppSettings.Instance.ResolutionAppendEnabled && movieNodes.Count > 0)
            ApplyResolutionAppendAsync(rootPath, movieNodes);
    }

    // ── Title Case Correction ─────────────────────────────────────────
    // Applies ToTitleCase to the title portion of each movie folder name
    // (split on last dash, title case the left side, preserve the right).
    // Acronyms in the user-defined list are restored after ToTitleCase.
    // A summary dialog lets the user review and selectively apply renames.
    private void ApplyTitleCaseCorrections(string rootPath, List<FolderNode> movieNodes)
    {
        var ti       = new CultureInfo("en-US").TextInfo;
        var acronyms = AppSettings.Instance.TitleCaseAcronyms;

        // Build list of proposed renames
        var proposals = new List<(FolderNode Node, string OldName, string NewName)>();

        foreach (var node in movieNodes)
        {
            var folderName = node.FolderPath; // FolderPath == folder name for movies
            var corrected  = ApplyTitleCaseToFolderName(folderName, ti, acronyms);

            if (!corrected.Equals(folderName, StringComparison.Ordinal))
                proposals.Add((node, folderName, corrected));
        }

        if (proposals.Count == 0) return;

        // Show summary dialog — user can uncheck items they don't want renamed
        ShowTitleCaseSummaryDialog(rootPath, proposals);
    }

    // Applies ToTitleCase to the title portion of a folder name.
    // Splits on the last dash to preserve category labels (e.g. "-featurette").
    // Restores acronyms that ToTitleCase would mangle.
    private static string ApplyTitleCaseToFolderName(
        string name, TextInfo ti, List<string> acronyms)
    {
        // Split on last dash — preserve label if it's a known category
        // For folder names we don't strip categories, but we do preserve
        // anything after the last dash as-is.
        var lastDash = name.LastIndexOf('-');
        string titlePart, labelPart;

        if (lastDash > 0)
        {
            titlePart = name.Substring(0, lastDash);
            labelPart = name.Substring(lastDash); // includes the dash
        }
        else
        {
            titlePart = name;
            labelPart = "";
        }

        var corrected = ti.ToTitleCase(titlePart.ToLower());

        // Restore acronyms — ToTitleCase will have title-cased them
        foreach (var acronym in acronyms)
        {
            // Replace title-cased version back with the correct form
            var titleCased = ti.ToTitleCase(acronym.ToLower());
            corrected = Regex.Replace(corrected, Regex.Escape(titleCased),
                acronym, RegexOptions.IgnoreCase);
        }

        return corrected + labelPart;
    }

    // Shows the title case summary dialog with per-item checkboxes.
    // Applies only the checked renames on OK.
    private void ShowTitleCaseSummaryDialog(
        string rootPath,
        List<(FolderNode Node, string OldName, string NewName)> proposals)
    {
        var win = new Window
        {
            Title                 = "Title Case Corrections",
            Width                 = 700,
            Height                = 400,
            MinWidth              = 500,
            MinHeight             = 200,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = (System.Windows.Media.Brush)FindResource("WindowBg"),
            ResizeMode            = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar         = false
        };

        var outer = new Grid { Margin = new Thickness(12) };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text         = "The following movie folder names will be renamed on disk to correct their capitalisation. Uncheck any you want to leave unchanged, then click Apply. Click Cancel to skip all renames.",
            Foreground   = (System.Windows.Media.Brush)FindResource("ForegroundColor"),
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(header, 0);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var stack = new StackPanel();

        // Build checkbox list — each item shows "OldName → NewName"
        var checkBoxes = new List<(CheckBox Cb, FolderNode Node, string OldName, string NewName)>();
        foreach (var (node, oldName, newName) in proposals)
        {
            var cb = new CheckBox
            {
                IsChecked  = true,
                Margin     = new Thickness(0, 3, 0, 3),
                Foreground = (System.Windows.Media.Brush)FindResource("ForegroundColor"),
                FontSize   = 12
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text       = oldName,
                Foreground = (System.Windows.Media.Brush)FindResource("SubtleForeground"),
                FontSize   = 12,
                Margin     = new Thickness(0, 0, 6, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text       = "→",
                Foreground = (System.Windows.Media.Brush)FindResource("SubtleForeground"),
                FontSize   = 12,
                Margin     = new Thickness(0, 0, 6, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text       = newName,
                Foreground = (System.Windows.Media.Brush)FindResource("ForegroundColor"),
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold
            });
            cb.Content = panel;
            stack.Children.Add(cb);
            checkBoxes.Add((cb, node, oldName, newName));
        }
        scroll.Content = stack;
        Grid.SetRow(scroll, 2);

        var btnPanel = new StackPanel
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
            Content   = "Apply",
            Width     = 80,
            Style     = (Style)FindResource("AccentButton"),
            IsDefault = true
        };
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        Grid.SetRow(btnPanel, 4);

        outer.Children.Add(header);
        outer.Children.Add(scroll);
        outer.Children.Add(btnPanel);
        win.Content = outer;

        cancelBtn.Click += (_, _) => { win.DialogResult = false; win.Close(); };

        // Capture checked state before closing — accessing IsChecked on controls
        // after the window closes may return unexpected values.
        List<(FolderNode Node, string OldName, string NewName)>? toApply = null;

        okBtn.Click += (_, _) =>
        {
            toApply = checkBoxes
                .Where(x => x.Cb.IsChecked == true)
                .Select(x => (x.Node, x.OldName, x.NewName))
                .ToList();
            win.DialogResult = true;
            win.Close();
        };

        if (win.ShowDialog() != true) return;
        if (toApply == null || toApply.Count == 0) return;

        // Apply checked renames
        foreach (var (node, oldName, newName) in toApply)
        {
            var oldPath = Path.Combine(rootPath, oldName);
            var newPath = Path.Combine(rootPath, newName);

            if (!Directory.Exists(oldPath)) continue;
            // On Windows paths are case-insensitive, so Directory.Exists(newPath)
            // returns true even when oldPath and newPath differ only in case.
            // Only skip if newPath exists AND is genuinely different from oldPath.
            if (Directory.Exists(newPath) &&
                !oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                // Windows is case-insensitive so Directory.Move fails when source
                // and destination differ only in case. Use a temp name as an
                // intermediate step to force the rename through.
                if (oldPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                {
                    var tempPath = newPath + "_tmp_rename_";
                    Directory.Move(oldPath, tempPath);
                    Directory.Move(tempPath, newPath);
                }
                else
                {
                    Directory.Move(oldPath, newPath);
                }
                node.DisplayName = newName;
                node.FolderPath  = newName;
                node.HasYearWarning = !YearPattern.IsMatch(newName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not rename folder:\n{oldName} → {newName}\n{ex.Message}",
                    "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    // ── Resolution Append ─────────────────────────────────────────────
    // Probes main title .mkv files (those matching the folder name) with
    // ffprobe and appends the resolution label if one is not already present.
    // Runs async so the UI stays responsive during probing.
    private async void ApplyResolutionAppendAsync(string rootPath, List<FolderNode> movieNodes)
    {
        var verifyAlways = AppSettings.Instance.ResolutionVerifyAlways;

        foreach (var node in movieNodes)
        {
            var folderName = node.FolderPath;
            var folderPath = Path.Combine(rootPath, folderName);

            try
            {
                var files = Directory.GetFiles(folderPath, "*.mkv");

                foreach (var filePath in files)
                {
                    var fileName    = Path.GetFileName(filePath);
                    var nameNoExt   = Path.GetFileNameWithoutExtension(fileName);

                    // Strip resolution suffixes and edition tags to get the
                    // bare title for comparison against the folder name.
                    var stripped = ResolutionSuffix.Replace(nameNoExt, "");
                    stripped = Regex.Replace(stripped, @"\s*\{edition-[^}]+\}", "",
                        RegexOptions.IgnoreCase).Trim();

                    // Only process files whose bare title matches the folder name
                    if (!stripped.Equals(folderName.Trim(),
                        StringComparison.OrdinalIgnoreCase)) continue;

                    // Skip if resolution already present and verify-always is off
                    var hasResolution = ResolutionSuffix.IsMatch(nameNoExt);
                    if (hasResolution && !verifyAlways) continue;

                    // Probe the file to get pixel height
                    var probe = await FfprobeService.ProbeFileAsync(filePath);
                    var video = probe.RemuxVideo.FirstOrDefault();
                    if (video == null) continue;

                    // Parse height from resolution string e.g. "1920x1080"
                    var resParts = video.Resolution.Split('x');
                    if (resParts.Length < 2 ||
                        !int.TryParse(resParts[1], out var height)) continue;

                    var label = DeriveResolutionLabel(height);
                    if (string.IsNullOrEmpty(label)) continue;

                    // If verifying always and resolution already matches, skip
                    if (hasResolution)
                    {
                        var existingMatch = ResolutionSuffix.Match(nameNoExt);
                        if (existingMatch.Success &&
                            existingMatch.Value.TrimStart('-')
                                .Equals(label, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // Build new filename: insert resolution before edition tag if present
                    string newNameNoExt;
                    var editionMatch = Regex.Match(nameNoExt,
                        @"(\s*\{edition-[^}]+\})$", RegexOptions.IgnoreCase);

                    if (editionMatch.Success)
                    {
                        // Insert before edition: "Title (Year) {edition-X}" →
                        //                        "Title (Year) {edition-X}-1080p"
                        // Per Plex docs edition comes before resolution suffix
                        newNameNoExt = nameNoExt + "-" + label;
                    }
                    else
                    {
                        // Strip any existing resolution suffix first if verify-always
                        var bare = hasResolution
                            ? ResolutionSuffix.Replace(nameNoExt, "")
                            : nameNoExt;
                        newNameNoExt = bare + "-" + label;
                    }

                    var newFileName = newNameNoExt + ".mkv";
                    var newFilePath = Path.Combine(folderPath, newFileName);

                    if (File.Exists(newFilePath)) continue;

                    try
                    {
                        File.Move(filePath, newFilePath);
                        RenameSettingsFiles(rootPath, folderName, fileName, newFileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Could not append resolution to:\n{fileName}\n{ex.Message}",
                            "Resolution Append Failed",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch { /* Skip folders we cannot read */ }
        }
    }

    // Maps ffprobe pixel height to a resolution label string.
    private static string DeriveResolutionLabel(int height) => height switch
    {
        >= 2160 => "2160p",
        >= 1080 => "1080p",
        >= 720  => "720p",
        >= 576  => "576p",
        >= 480  => "480p",
        _       => ""
    };

    // Renames matching .txt settings files in both Remux and Transcode
    // folders when a .mkv file is renamed. Shared by title case and
    // resolution append rename operations.
    private void RenameSettingsFiles(
        string rootPath, string folderName,
        string oldFileName, string newFileName)
    {
        var oldTxt = Path.GetFileNameWithoutExtension(oldFileName) + ".txt";
        var newTxt = Path.GetFileNameWithoutExtension(newFileName) + ".txt";

        foreach (var mode in new[] { "Remux", "Transcode" })
        {
            var settingsDir = Path.Combine(rootPath, mode, folderName);
            var oldTxtPath  = Path.Combine(settingsDir, oldTxt);
            var newTxtPath  = Path.Combine(settingsDir, newTxt);

            if (!File.Exists(oldTxtPath)) continue;

            try
            {
                // Windows is case-insensitive — two-step rename needed for case-only changes.
                if (newTxt.Equals(oldTxt, StringComparison.OrdinalIgnoreCase))
                {
                    var tempPath = oldTxtPath + "_tmp_rename_";
                    File.Move(oldTxtPath, tempPath);
                    File.Move(tempPath, newTxtPath);
                }
                else
                {
                    File.Move(oldTxtPath, newTxtPath);
                }
            }
            catch { /* Best effort — don't block the rename */ }
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

        // TV season files should never show the year warning — episode filenames
        // don't follow the "Title (YYYY)" movie naming convention.
        var isTvSeason = e.NewValue is SeasonNode;

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
                    DisplayName    = displayName,
                    FileName       = fileName,
                    GroupKey       = category,
                    Background     = hasSettings
                        ? System.Windows.Media.Brushes.Green
                        : System.Windows.Media.Brushes.Transparent,
                    // Only flag year warning on movie main-title files (no category suffix).
                    // TV episode files under a SeasonNode are exempt — they don't follow
                    // the "Title (YYYY)" movie naming convention.
                    HasYearWarning = !isTvSeason && category == null && !YearPattern.IsMatch(nameNoExt)
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

        // ── Settings mismatch detection ───────────────────────────────
        // Compare the saved command against current app preferences.
        // DoVi streams are exempt — preset and quality flags don't apply
        // to the copy-only path.
        if (video != null && !video.HasDoVi)
        {
            // Preset mismatch: saved preset vs current default
            var defaultPreset = AppSettings.Instance.DefaultPreset ?? "";
            video.PresetMismatch = !string.IsNullOrWhiteSpace(defaultPreset) &&
                !video.Preset.Equals(defaultPreset, StringComparison.OrdinalIgnoreCase);

            // Quality flags mismatch: parse app flags into "-flag value" pairs
            // and check each pair exists as a substring in the saved command.
            // e.g. ["-cq 19", "-spatial-aq 1", "-aq-strength 10"]
            var appFlags = AppSettings.Instance.NvencQualityFlags ?? "";
            video.QualityFlagsMismatch = false;

            if (!string.IsNullOrWhiteSpace(appFlags))
            {
                var flagPairs = ParseFlagPairs(appFlags);
                var commandLower = command.ToLowerInvariant();
                foreach (var pair in flagPairs)
                {
                    if (!commandLower.Contains(pair.ToLowerInvariant()))
                    {
                        video.QualityFlagsMismatch = true;
                        break;
                    }
                }
            }
        }

        RefreshVideoWarningColumn();
    }

    // Parses a quality flags string into "-flag value" pairs.
    // e.g. "-cq 19 -spatial-aq 1 -aq-strength 10"
    //   → ["-cq 19", "-spatial-aq 1", "-aq-strength 10"]
    // Splits on flag boundaries (tokens starting with '-'), grouping
    // each flag with its immediately following value token.
    // Standalone flags with no value (e.g. boolean switches) are kept as-is.
    private static List<string> ParseFlagPairs(string flagString)
    {
        var pairs  = new List<string>();
        var tokens = flagString.Trim().Split(' ',
            StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].StartsWith("-")) continue;

            // If the next token exists and is not itself a flag, it's the value
            if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("-"))
            {
                pairs.Add($"{tokens[i]} {tokens[i + 1]}");
                i++; // skip the value token
            }
            else
            {
                pairs.Add(tokens[i]);
            }
        }

        return pairs;
    }

    // Adds or removes the Warning column in TranscodeVideoList based on whether
    // any video track has QualityFlagsMismatch = true.
    // The column is added dynamically so it only appears when relevant.
    private void RefreshVideoWarningColumn()
    {
        if (TranscodeVideoList.View is not GridView gv) return;

        const string warningHeader = "Warning";
        var existing = gv.Columns.FirstOrDefault(c =>
            c.Header?.ToString() == warningHeader);

        var hasMismatch = TranscodeVideoTracks.Any(t => t.QualityFlagsMismatch);

        if (hasMismatch && existing == null)
        {
            // Add the Warning column dynamically
            var col = new GridViewColumn
            {
                Header = warningHeader,
                Width  = 260
            };

            var template = new DataTemplate();
            var factory  = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetValue(TextBlock.TextProperty,
                "NVENC Quality Flags differ from App Settings");
            factory.SetValue(TextBlock.ForegroundProperty,
                new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)));
            factory.SetValue(TextBlock.FontSizeProperty, 11.0);
            factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            factory.SetValue(TextBlock.VisibilityProperty,
                new System.Windows.Data.Binding("QualityFlagsMismatch")
                {
                    Converter = new BooleanToVisibilityConverter()
                });

            template.VisualTree = factory;
            col.CellTemplate    = template;
            gv.Columns.Add(col);
        }
        else if (!hasMismatch && existing != null)
        {
            gv.Columns.Remove(existing);
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

        // Remove the Warning column if it was showing for the previous file.
        RefreshVideoWarningColumn();

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
        var dragEnabled   = selectedCount >= 2;
        RemuxAudioList.AllowDrop = dragEnabled;
        // Show the tooltip only when drag is disabled — once 2+ tracks are
        // selected it disappears since the feature is now available.
        RemuxAudioList.ToolTip = dragEnabled
            ? null
            : "Select 2 or more tracks to enable reordering";
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

        // No-op if the name hasn't changed at all (case-sensitive comparison so
        // that a case-only rename — e.g. "movie.mkv" → "Movie.mkv" — is allowed through).
        if (newFileName.Equals(oldFileName, StringComparison.Ordinal)) return;

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

        // Skip the conflict check for case-only renames — File.Exists is case-insensitive
        // on Windows and would always fire a false positive in that case.
        var isCaseOnlyRename = newFileName.Equals(oldFileName, StringComparison.OrdinalIgnoreCase);
        if (!isCaseOnlyRename && File.Exists(newFilePath))
        {
            MessageBox.Show($"A file named \"{newFileName}\" already exists in this folder.",
                "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Rename the .mkv file ──────────────────────────────────────
        try
        {
            // Windows is case-insensitive so File.Move fails when source
            // and destination differ only in case. Use a temp name first.
            if (isCaseOnlyRename)
            {
                var tempPath = oldFilePath + "_tmp_rename_";
                File.Move(oldFilePath, tempPath);
                File.Move(tempPath, newFilePath);
            }
            else
            {
                File.Move(oldFilePath, newFilePath);
            }
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

        // ── Always build from current UI state ────────────────────────
        // View Command always shows what would be generated right now.
        // Blocking conditions (no output directory, no audio selected)
        // fire normally — same UX as Save Transcode Settings.
        if (string.IsNullOrWhiteSpace(OutputDirectoryBox.Text))
        {
            MessageBox.Show("No output directory chosen.",
                "View Command", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string command;
        try
        {
            command = _isTranscodeMode
                ? CommandBuilder.BuildTranscodeCommand(
                    OutputDirectoryBox.Text, _selectedMovieFolder, leaf.FileName,
                    _inputDirectory, TranscodeVideoTracks, TranscodeAudioTracks,
                    TranscodeSubtitleTracks)
                : CommandBuilder.BuildRemuxCommand(
                    OutputDirectoryBox.Text, _selectedMovieFolder, leaf.FileName,
                    _inputDirectory, RemuxAudioTracks, RemuxSubtitleTracks);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "View Command",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── Compare to saved settings file if one exists ──────────────
        // If the current command differs from what's saved, warn the user
        // so they know the saved file is out of date.
        var showStaleWarning = false;
        var modeFolder   = _isTranscodeMode ? "Transcode" : "Remux";
        var nameNoExt    = Path.GetFileNameWithoutExtension(leaf.FileName);
        var settingsFile = Path.Combine(_inputDirectory, modeFolder,
                               _selectedMovieFolder, nameNoExt + ".txt");

        if (File.Exists(settingsFile))
        {
            var savedCommand = File.ReadAllText(settingsFile, System.Text.Encoding.UTF8);
            showStaleWarning = !string.Equals(
                command.Trim(), savedCommand.Trim(),
                StringComparison.Ordinal);
        }

        ShowCommandPreview(command, leaf.FileName, showStaleWarning);
    }

    // Opens a small owned window showing the full command string in a
    // selectable, word-wrapped TextBox with a Copy button.
    // showStaleWarning: true when the displayed command was read from a settings
    // file that differs from the current UI state — prompts the user to re-save.
    private void ShowCommandPreview(string command, string fileName, bool showStaleWarning = false)
    {
        // ── Window shell ──────────────────────────────────────────────
        var win = new Window
        {
            Title           = $"Command — {fileName}",
            Width           = 800,
            Height          = showStaleWarning ? 340 : 300,
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

        var rowIdx = 0;

        // Warning row (only added when needed)
        if (showStaleWarning)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });

            var warning = new TextBlock
            {
                Text         = "⚠ The current selections differ from the saved settings file. " +
                               "This command shows what will be executed. " +
                               "Use Save Transcode Settings to update it.",
                TextWrapping = TextWrapping.Wrap,
                Foreground   = new System.Windows.Media.SolidColorBrush(
                                   System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)),
                FontSize     = 12,
                Padding      = new Thickness(4, 2, 4, 2)
            };
            Grid.SetRow(warning, rowIdx++);
            grid.Children.Add(warning);
            rowIdx++; // skip spacer row
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Command TextBox ───────────────────────────────────────────
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
        Grid.SetRow(textBox, rowIdx++);
        rowIdx++; // skip spacer row

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
        Grid.SetRow(copyBtn, rowIdx);

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

    // ── Transcode Audio: derived sub-row management ───────────────────

    // Called when the user clicks + on a lossless parent audio row.
    // Inserts a new derived lossy sub-row immediately after the parent.
    // The sub-row inherits the parent's stream index as its source and
    // defaults to eac3 at 640 kbps — both changeable by the user.
    // Multiple sub-rows can be added to the same parent (e.g. 5.1 + stereo).
    private void AddDerivedRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TranscodeAudioTrack parent)
            return;

        var subRow = new TranscodeAudioTrack
        {
            OriginalTrackIndex = parent.OriginalTrackIndex,
            IsSubRow           = true,
            IsLossless         = false,
            ParentTrackIndex   = parent.OriginalTrackIndex,
            IsSelected         = true,
            TrackInfo          = $"  ↳ derived from {parent.TrackInfo}",
            Format             = "eac3",
            Width              = "Keep",
            BitRate            = "640"
        };

        // Insert immediately after the last existing sub-row for this parent,
        // or directly after the parent if it has none yet.
        var parentIdx = TranscodeAudioTracks.IndexOf(parent);
        var insertAt  = parentIdx + 1;
        while (insertAt < TranscodeAudioTracks.Count &&
               TranscodeAudioTracks[insertAt].IsSubRow &&
               TranscodeAudioTracks[insertAt].ParentTrackIndex == parent.OriginalTrackIndex)
        {
            insertAt++;
        }

        TranscodeAudioTracks.Insert(insertAt, subRow);
    }

    // Called when the user clicks — on a sub-row.
    // Removes that sub-row from the collection.
    private void RemoveDerivedRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TranscodeAudioTrack subRow)
            return;

        TranscodeAudioTracks.Remove(subRow);
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

// ── InverseBoolConverter ──────────────────────────────────────────────
// Converts bool → bool by negating it.
// Used to disable dropdowns when HasDoVi is true:
//   IsEnabled="{Binding HasDoVi, Converter={StaticResource InverseBoolConverter}}"
// When HasDoVi=true → IsEnabled=false (locked).
// When HasDoVi=false → IsEnabled=true (normal).
public class InverseBoolConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => value is bool b && !b;
}

// ── PresetMismatchBrushConverter ──────────────────────────────────────
// Converts PresetMismatch bool → Brush for the Preset ComboBox Background.
// When true → amber (#FFF3CD) fill to indicate the saved preset differs
// from the current app default. When false → transparent (theme default).
public class PresetMismatchBrushConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => value is bool b && b
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07))
            : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}
