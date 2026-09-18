// ============================================================
// RipView.xaml.cs
// ------------------------------------------------------------
// Rip mode's content. First pass: layout plus the parts that
// work without the ripping engine -- the Destination picker
// with its history, and the list of optical drives. The disc
// list, disc titles, chips, picture and jobs are empty until
// the model classes are wired in.
// ============================================================

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace TranscodeTools;

public partial class RipView : UserControl
{
    // ── Drives box ───────────────────────────────────────────────────
    public ObservableCollection<OpticalDriveRow> Drives { get; } = new();

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

    public RipView()
    {
        InitializeComponent();
        DriveList.ItemsSource = Drives;
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

    // ── Drives ───────────────────────────────────────────────────────
    // Windows reports a mounted ISO as an optical drive too, so a mounted
    // image shows up in this list for now.

    private async Task RefreshDrivesAsync()
    {
        int ticket = ++_driveRefreshTicket;
        StatusText.Text = "Checking optical drives";

        // Reading a drive that is spinning up can block for seconds, so the
        // scan runs off the UI thread.
        var rows = await Task.Run(() => DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom)
            .Select(d => new OpticalDriveRow(d.Name.TrimEnd('\\'), ReadDiscLabel(d)))
            .ToList());

        if (ticket != _driveRefreshTicket) return;

        Drives.Clear();
        foreach (var row in rows)
            Drives.Add(row);

        StatusText.Text = rows.Count switch
        {
            0 => "No optical drives found.",
            1 => "1 optical drive found.",
            _ => $"{rows.Count} optical drives found."
        };
    }

    private static string ReadDiscLabel(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady) return "No disc";
            return string.IsNullOrEmpty(drive.VolumeLabel) ? "(no label)" : drive.VolumeLabel;
        }
        catch (IOException)
        {
            return "No disc";
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
        if (DiscList.SelectedItem is null) return;
        // The disc name comes from the disc record once the models are wired.
        ShowDiscTitles(DiscList.SelectedItem.ToString() ?? "");
    }

    private void ShowDiscTitles(string discName)
    {
        SelectedDiscName.Text       = discName;
        DiscTitlesHeader.Visibility = Visibility.Visible;
        DiscList.Visibility         = Visibility.Collapsed;
        TitleList.Visibility        = Visibility.Visible;
    }

    private void BackToDiscs_Click(object sender, RoutedEventArgs e)
    {
        DiscTitlesHeader.Visibility = Visibility.Collapsed;
        TitleList.Visibility        = Visibility.Collapsed;
        DiscList.Visibility         = Visibility.Visible;
        DiscList.SelectedItem       = null;
    }
}

// One row in the drives box.
public sealed record OpticalDriveRow(string Letter, string Label);
