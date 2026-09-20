// ============================================================
// RipView.xaml.cs
// ------------------------------------------------------------
// Rip mode's content. The Destination picker and its history,
// the optical drive list, and the disc list bound to the disc
// records under <Destination>\Discs\. Selecting a disc lists its
// disc titles; selecting a disc title shows a chip per menu
// screen that names it; clicking a chip shows that screen with
// its buttons outlined and the naming button highlighted.
//
// Start runs the disc read: the IFO parse (port seam 1) on a
// background thread, reported through a row in the jobs list, and
// saved as the disc's record. The rest of the engine -- buttons,
// frames, MakeMKV -- joins the same job as each seam lands.
// ============================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace TranscodeTools;

public partial class RipView : UserControl
{
    // ── Collections the view binds to ────────────────────────────────
    public ObservableCollection<OpticalDriveRow> Drives { get; } = new();
    public ObservableCollection<DiscRecord>      Discs  { get; } = new();
    public ObservableCollection<ScreenChip>      Chips  { get; } = new();
    public ObservableCollection<RipJob>          Jobs   { get; } = new();

    // The Destination currently loaded, or "" before one is chosen.
    private string _destination = "";

    // The disc whose titles are listed, while the left column shows titles.
    private DiscRecord? _selectedDisc;

    // Each refresh takes a ticket. A slow refresh that finishes after a
    // newer one started throws its result away, so the list always shows
    // the latest scan.
    private int _driveRefreshTicket;

    // The window's message hook, used to hear about discs being inserted
    // or ejected. Held so it can be removed when the view unloads.
    private HwndSource? _hwndSource;

    // Guards DestinationBox_SelectionChanged while the history list is
    // being repopulated from code (same pattern as the Input/Output boxes).
    private bool _suppressDestinationSelection;

    // Drive letters with a job running on them. One job per drive: the
    // drive's Start stays disabled until its job ends.
    private readonly HashSet<string> _busyDrives = new(StringComparer.OrdinalIgnoreCase);

    private const string NoTitleSelectedText =
        "Select a disc title to see the menu screen it was named from.";

    public RipView()
    {
        InitializeComponent();
        DriveList.ItemsSource = Drives;
        DiscList.ItemsSource  = Discs;
        ChipStrip.ItemsSource = Chips;
        JobList.ItemsSource   = Jobs;
        ShowPlaceholder(NoTitleSelectedText);
    }

    // Loaded fires once the view is in the window, even while it is
    // collapsed, so the drive list is ready before Rip mode is shown.
    private void RipView_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshDestinationHistory();
        HookDeviceChanges();
        _ = RefreshDrivesAsync();
    }

    private void RipView_Unloaded(object sender, RoutedEventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    // ── Destination ──────────────────────────────────────────────────
    // Public so the main window's File menu can call them.

    public void BrowseDestination()
    {
        var dialog = new OpenFolderDialog { Title = "Select Destination" };
        if (dialog.ShowDialog() != true) return;
        LoadDestination(dialog.FolderName);
    }

    public void ClearDestinationHistory()
    {
        var result = MessageBox.Show(
            "Clear the destination directory history?",
            "Clear Destination History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        AppSettings.Instance.RecentDestinationFolders.Clear();
        AppSettings.Instance.Save();
        RefreshDestinationHistory();
    }

    private void DestinationButton_Click(object sender, RoutedEventArgs e) => BrowseDestination();

    private void DestinationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDestinationSelection) return;
        if (DestinationBox.SelectedItem is not string selected) return;
        LoadDestination(selected);
    }

    // Shared by Browse and history selection so both behave the same.
    private void LoadDestination(string folder)
    {
        AppSettings.Instance.AddRecentDestination(folder);
        AppSettings.Instance.Save();
        RefreshDestinationHistory(folder);

        _destination = folder;
        LoadDiscs();
        UpdateStartButtons();
    }

    // Repopulates the dropdown from saved history. Text is set inside the
    // suppress block; clearing SelectedItem instead would also clear Text
    // on an editable ComboBox (see RefreshHistoryDropdowns in MainWindow).
    private void RefreshDestinationHistory(string? text = null)
    {
        _suppressDestinationSelection = true;

        var current = text ?? DestinationBox.Text;
        DestinationBox.ItemsSource = AppSettings.Instance.RecentDestinationFolders.ToList();
        DestinationBox.Text = current;

        _suppressDestinationSelection = false;
    }

    // ── Disc records ─────────────────────────────────────────────────

    private void LoadDiscs()
    {
        ShowDiscList();
        Discs.Clear();

        var problems = new List<string>();
        foreach (var disc in DiscStore.LoadAll(_destination, problems))
            Discs.Add(disc);

        var loaded = Discs.Count == 1 ? "1 disc loaded." : $"{Discs.Count} discs loaded.";
        StatusText.Text = problems.Count == 0
            ? loaded
            : $"{loaded} Could not read {problems.Count}: {string.Join("; ", problems)}";
        StatusText.ToolTip = problems.Count == 0 ? null : string.Join("\n", problems);
    }

    // ── Drives ───────────────────────────────────────────────────────
    // Windows reports a mounted ISO as an optical drive too, so a mounted
    // image shows up in this list for now.

    private async Task RefreshDrivesAsync()
    {
        int ticket = ++_driveRefreshTicket;

        // Reading a drive that is spinning up can block for seconds, so the
        // scan runs off the UI thread.
        var rows = await Task.Run(() => DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom)
            .Select(d => ReadDrive(d))
            .ToList());

        if (ticket != _driveRefreshTicket) return;

        Drives.Clear();
        foreach (var row in rows)
            Drives.Add(row);
        UpdateStartButtons();

        // The disc count from LoadDiscs takes the status line once a
        // Destination is loaded; until then it reports the drives and
        // says what is needed before a disc can start.
        if (_destination.Length == 0)
        {
            var found = rows.Count switch
            {
                0 => "No optical drives found.",
                1 => "1 optical drive found.",
                _ => $"{rows.Count} optical drives found."
            };
            StatusText.Text = rows.Count == 0 ? found : $"{found} Choose a Destination to start a disc.";
        }
    }

    // Sets each drive row's Start button: enabled when the drive has a
    // disc and no job is running for it. The tooltip carries the reason
    // when it is disabled.
    //
    // A missing Destination deliberately does NOT disable Start. A
    // disabled button raises no click, so the only thing it could offer
    // is a tooltip, and that is too quiet for the one mistake that stops
    // everything. Start stays live and says what is wrong.
    private void UpdateStartButtons()
    {
        foreach (var drive in Drives)
        {
            if (!drive.HasDisc)
                drive.SetStart(false, "No disc in this drive.");
            else if (_busyDrives.Contains(drive.Letter))
                drive.SetStart(false, $"{drive.Letter} is already running.");
            else
                drive.SetStart(true, $"Start {drive.Label}.");
        }
    }

    // ── Start ────────────────────────────────────────────────────────

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpticalDriveRow drive) return;

        // Each refusal names itself on the status line. The button's own
        // IsEnabled already covers these, but if the two ever disagree a
        // silent return leaves a button that does nothing at all.
        if (!drive.HasDisc)
        {
            StatusText.Text = $"{drive.Letter} reports no disc. Eject and reinsert it, or wait for the drive to spin up.";
            return;
        }
        if (_destination.Length == 0)
        {
            MessageBox.Show(
                "Please set a destination folder.",
                "No Destination",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (_busyDrives.Contains(drive.Letter))
        {
            StatusText.Text = $"{drive.Letter} is already running.";
            return;
        }

        // The disc's name is its volume label: it names the folder under
        // Discs\ and Rip\ and the ISO file, so a label that cannot be a
        // folder name has to stop here rather than half way through.
        var name = drive.Label;
        if (name == "(no label)" || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText.Text = $"{drive.Letter} cannot be started: \"{name}\" cannot be used as a folder name.";
            return;
        }

        var existing = DiscStore.TryLoad(_destination, name);
        if (existing != null && !ConfirmReplace(existing))
        {
            StatusText.Text = $"{name} left as it was.";
            return;
        }

        var job = new RipJob(name, drive.Letter) { Status = "starting" };
        Jobs.Add(job);
        _busyDrives.Add(drive.Letter);
        UpdateStartButtons();

        // Progress is created here, on the UI thread, so its callbacks
        // come back to the UI thread and can touch the job's bindings.
        var progress = new Progress<string>(message => job.Status = message);
        var source   = drive.Letter + System.IO.Path.DirectorySeparatorChar;

        try
        {
            var record = await Task.Run(() =>
            {
                var disc = DiscAnalysis.ReadDisc(name, source, progress, job.Token);
                DiscStore.Save(_destination, disc);
                return disc;
            }, job.Token);

            job.Finish($"{Count(record.Titles.Count, "title")}, {Count(record.Screens.Count, "screen")}", false);
            LoadDiscs();
        }
        catch (OperationCanceledException)
        {
            job.Finish("Cancelled.", false);
        }
        catch (Exception ex)
        {
            // Every failure is visible and carries the program's own
            // message rather than a house one that hides it.
            job.Finish(ex.Message, true);
        }
        finally
        {
            _busyDrives.Remove(drive.Letter);
            UpdateStartButtons();
        }
    }

    // Reading a disc that already has a record replaces it, so it asks
    // first and says what is being replaced.
    private static bool ConfirmReplace(DiscRecord existing)
    {
        var answer = MessageBox.Show(
            $"{existing.Name} already has a record: " +
            $"{Count(existing.Titles.Count, "title")}, {Count(existing.Screens.Count, "screen")}, " +
            $"read {existing.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}.\n\n" +
            "Read the disc again and replace it?",
            "Disc Already Read",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return answer == MessageBoxResult.Yes;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RipJob job)
            job.Cancel();
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static OpticalDriveRow ReadDrive(DriveInfo drive)
    {
        var letter = drive.Name.TrimEnd('\\');
        try
        {
            if (!drive.IsReady) return new OpticalDriveRow(letter, "No disc", hasDisc: false);
            var label = string.IsNullOrEmpty(drive.VolumeLabel) ? "(no label)" : drive.VolumeLabel;
            return new OpticalDriveRow(letter, label, hasDisc: true);
        }
        catch (IOException)
        {
            return new OpticalDriveRow(letter, "No disc", hasDisc: false);
        }
    }

    // Windows broadcasts WM_DEVICECHANGE to every top-level window when a
    // disc is inserted (DBT_DEVICEARRIVAL) or ejected (DBT_DEVICEREMOVECOMPLETE).
    // Listening on the main window's handle is enough; no registration needed
    // for volume changes.
    private const int WM_DEVICECHANGE          = 0x0219;
    private const int DBT_DEVICEARRIVAL        = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private void HookDeviceChanges()
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            long evt = wParam.ToInt64();
            if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
                _ = RefreshDrivesAsync();
        }
        return IntPtr.Zero;
    }

    // ── Left column: discs -> disc titles ────────────────────────────

    private void DiscList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscList.SelectedItem is not DiscRecord disc) return;
        ShowDiscTitles(disc);
    }

    private void ShowDiscTitles(DiscRecord disc)
    {
        _selectedDisc = disc;
        SelectedDiscName.Text  = disc.Name;
        TitleList.ItemsSource  = disc.Titles;
        TitleList.SelectedItem = null;

        DiscTitlesHeader.Visibility = Visibility.Visible;
        DiscList.Visibility         = Visibility.Collapsed;
        TitleList.Visibility        = Visibility.Visible;
    }

    private void BackToDiscs_Click(object sender, RoutedEventArgs e) => ShowDiscList();

    private void ShowDiscList()
    {
        _selectedDisc = null;
        TitleList.ItemsSource = null;
        Chips.Clear();
        ShowPlaceholder(NoTitleSelectedText);

        DiscTitlesHeader.Visibility = Visibility.Collapsed;
        TitleList.Visibility        = Visibility.Collapsed;
        DiscList.Visibility         = Visibility.Visible;
        DiscList.SelectedItem       = null;
    }

    // Right-click on a disc row: open its rip folder. The menu item's
    // DataContext is the row's DiscRecord, inherited from the row.
    private void OpenDiscFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DiscRecord disc) return;

        if (!Directory.Exists(disc.RipFolder))
        {
            MessageBox.Show(
                $"The rip folder for {disc.Name} was not found:\n{disc.RipFolder}",
                "Folder Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        Process.Start("explorer.exe", $"\"{disc.RipFolder}\"");
    }

    // ── Chip strip ───────────────────────────────────────────────────
    // One chip per screen + button set that names the selected title, in
    // the order found (NamedBy keeps that order). Two buttons on the same
    // set naming the same title share one chip and are both highlighted.

    private void TitleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Chips.Clear();

        if (TitleList.SelectedItem is not TitleRecord title || _selectedDisc is null)
        {
            ShowPlaceholder(NoTitleSelectedText);
            return;
        }

        foreach (var source in title.NamedBy)
        {
            var existing = Chips.FirstOrDefault(c => c.Matches(source));
            if (existing != null)
            {
                existing.Highlight.Add(source.Button);
                continue;
            }

            var screen = _selectedDisc.Screens.FirstOrDefault(s =>
                s.Ifo == source.Ifo && s.Pgc == source.Pgc && s.Cell == source.Cell);
            var set = screen?.ButtonSets.FirstOrDefault(b => b.Number == source.ButtonSet);

            Chips.Add(new ScreenChip(source, screen, set));
        }

        if (Chips.Count == 0)
        {
            ShowPlaceholder("No menu screen names this title.");
            return;
        }

        // Show the first screen straight away and mark its chip. ShowScreen
        // is called directly because the chip's RadioButton may not exist
        // yet; when it is created, its Checked event shows the same screen.
        Chips[0].IsSelected = true;
        ShowScreen(Chips[0]);
    }

    private void Chip_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ScreenChip chip)
            ShowScreen(chip);
    }

    // ── Picture + hotspots ───────────────────────────────────────────

    private void ShowScreen(ScreenChip chip)
    {
        if (_selectedDisc is null) return;

        // Every gap is shown, never skipped: a record that points at a
        // screen or picture that isn't there says so in the picture area.
        if (chip.Screen is null || chip.Set is null)
        {
            ShowPlaceholder($"The record names {chip.ToolTip}, but that screen is not in the record.");
            return;
        }
        if (string.IsNullOrEmpty(chip.Screen.Image))
        {
            ShowPlaceholder($"No picture was saved for {chip.ToolTip}.");
            return;
        }

        var path = System.IO.Path.Combine(
            DiscStore.DiscFolder(_destination, _selectedDisc.Name), chip.Screen.Image);

        BitmapImage bitmap;
        try
        {
            // OnLoad reads the file fully and releases it, so the picture
            // file is never held open while it is shown.
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource   = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            ShowPlaceholder($"The picture for {chip.ToolTip} could not be read:\n{path}\n{ex.Message}");
            return;
        }

        // Size the frame to the picture's own pixels so button rectangles,
        // which are in those pixels, line up for NTSC and PAL alike.
        PictureFrame.Width  = bitmap.PixelWidth;
        PictureFrame.Height = bitmap.PixelHeight;
        MenuImage.Source    = bitmap;

        DrawHotspots(chip);

        PicturePlaceholder.Visibility = Visibility.Collapsed;
        PictureBox.Visibility         = Visibility.Visible;
    }

    // Outlines every button in the set and highlights the ones that name
    // the selected title. The shapes are made in code, so implicit styles
    // don't reach them; brushes are set as resource references instead,
    // which also keeps them following a light/dark theme switch.
    private void DrawHotspots(ScreenChip chip)
    {
        HotspotLayer.Children.Clear();
        if (chip.Set is null) return;

        foreach (var button in chip.Set.Buttons)
        {
            bool highlight = chip.Highlight.Contains(button.Number);

            var box = new Rectangle
            {
                Width           = Math.Max(1, button.X1 - button.X0),
                Height          = Math.Max(1, button.Y1 - button.Y0),
                StrokeThickness = highlight ? 3 : 1,
            };
            box.SetResourceReference(Shape.StrokeProperty, highlight ? "AccentColor" : "SubtleForeground");
            Canvas.SetLeft(box, button.X0);
            Canvas.SetTop(box, button.Y0);
            HotspotLayer.Children.Add(box);

            var number = new TextBlock
            {
                Text       = button.Number.ToString(),
                FontSize   = 12,
                FontWeight = highlight ? FontWeights.Bold : FontWeights.Normal,
                Padding    = new Thickness(3, 0, 3, 0),
            };
            number.SetResourceReference(TextBlock.ForegroundProperty, highlight ? "AccentColor" : "ForegroundColor");
            number.SetResourceReference(TextBlock.BackgroundProperty, "PanelBg");
            Canvas.SetLeft(number, button.X0);
            Canvas.SetTop(number, button.Y0);
            HotspotLayer.Children.Add(number);
        }
    }

    private void ShowPlaceholder(string text)
    {
        PictureBox.Visibility         = Visibility.Collapsed;
        MenuImage.Source              = null;
        HotspotLayer.Children.Clear();
        PicturePlaceholder.Text       = text;
        PicturePlaceholder.Visibility = Visibility.Visible;
    }
}

// One row in the drives box. CanStart and StartHint change when a
// Destination is chosen, so they notify the Start button's bindings.
public sealed class OpticalDriveRow : INotifyPropertyChanged
{
    public string Letter  { get; }
    public string Label   { get; }
    public bool   HasDisc { get; }

    public OpticalDriveRow(string letter, string label, bool hasDisc)
    {
        Letter  = letter;
        Label   = label;
        HasDisc = hasDisc;
    }

    public bool   CanStart  { get; private set; }
    public string StartHint { get; private set; } = "";

    public void SetStart(bool canStart, string hint)
    {
        CanStart  = canStart;
        StartHint = hint;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStart)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartHint)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// One chip in the strip above the picture: a screen and one of its
// button sets, plus the buttons on it that name the selected title.
public sealed class ScreenChip : INotifyPropertyChanged
{
    public NamedBySource    Source { get; }
    public ScreenRecord?    Screen { get; }
    public ButtonSetRecord? Set    { get; }
    public HashSet<int>     Highlight { get; } = new();

    public string Label   { get; }
    public string ToolTip { get; }

    public ScreenChip(NamedBySource source, ScreenRecord? screen, ButtonSetRecord? set)
    {
        Source = source;
        Screen = screen;
        Set    = set;
        Highlight.Add(source.Button);

        var ifo = System.IO.Path.GetFileNameWithoutExtension(source.Ifo);
        Label   = source.ButtonSet > 1 ? $"{ifo} pgc {source.Pgc} set {source.ButtonSet}"
                                       : $"{ifo} pgc {source.Pgc}";
        ToolTip = $"{source.Ifo} pgc {source.Pgc} cell {source.Cell} button set {source.ButtonSet}";
    }

    public bool Matches(NamedBySource other) =>
        Source.Ifo == other.Ifo && Source.Pgc == other.Pgc &&
        Source.Cell == other.Cell && Source.ButtonSet == other.ButtonSet;

    // Bound two-way to the chip's RadioButton, so the code can select a
    // chip and the strip shows it as checked.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
