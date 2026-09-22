// ============================================================
// RipView.xaml.cs
// ------------------------------------------------------------
// Rip mode's content. The Destination picker and its history,
// the optical drive list, and the disc list bound to the disc
// records under <Destination>\Rip\<DISC>\Menu\. Selecting a disc
// lists its disc titles; selecting a disc title shows a chip per
// menu screen that names it; clicking a chip shows that screen with
// its buttons outlined and the naming button highlighted.
//
// The drive row owns the backup. Its button reads Backup, then
// Cancel while MakeMKV runs (the row fills green with MakeMKV's own
// progress), then Eject on success or Log on failure (the row turns
// red). Nothing appears in the jobs list until a backup completes.
//
// Clicking Backup checks disk space first (DiskSpace): too little and
// a message box refuses it. A running backup holds its space in a
// reserve file that shrinks as the ISO grows.
//
// A completed backup adds a row to the jobs list with a Start
// button. Start runs the disc read from the backup ISO, not the
// drive: the ISO is mounted with no drive letter (IsoMount), the
// IFO parse (port seam 1) runs on a background thread, the record
// is saved, and the ISO is unmounted. The disc is not needed after
// its backup, so Eject is safe at any time. The ISO's IFOs were
// measured byte-identical to the disc's. The rest of the engine
// joins the same job as each seam lands.
// ============================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

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

    // ISOs with a job reading them. A second job on the same ISO waits
    // its turn: both would share one mount, and the first to finish
    // would unmount it under the other.
    private readonly HashSet<string> _busyIsos = new(StringComparer.OrdinalIgnoreCase);

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

        // Reserve files left by a power cut; ones a running backup holds
        // refuse deletion and stay.
        DiskSpace.SweepReserve(folder);

        LoadDiscs();
        RebuildJobs();
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
        var readings = await Task.Run(() => DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom)
            .Select(d => ReadDrive(d))
            .ToList());

        if (ticket != _driveRefreshTicket) return;

        // Rows are updated in place rather than rebuilt: a row carries its
        // backup, and a disc inserted in another drive must not reset it.
        foreach (var row in Drives.ToList())
            if (!readings.Any(r => r.Letter.Equals(row.Letter, StringComparison.OrdinalIgnoreCase)) &&
                row.State != DriveState.BackingUp)
                Drives.Remove(row);

        foreach (var reading in readings)
        {
            var row = Drives.FirstOrDefault(r => r.Letter.Equals(reading.Letter, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                row.Update(reading.Label, reading.HasDisc);
                continue;
            }

            row = new OpticalDriveRow(reading.Letter, reading.Label, reading.HasDisc);
            int at = 0;
            while (at < Drives.Count && string.Compare(Drives[at].Letter, row.Letter, StringComparison.OrdinalIgnoreCase) < 0)
                at++;
            Drives.Insert(at, row);
        }

        // The disc count from LoadDiscs takes the status line once a
        // Destination is loaded; until then it reports the drives and
        // says what is needed before a disc can start.
        if (_destination.Length == 0)
        {
            var found = readings.Count switch
            {
                0 => "No optical drives found.",
                1 => "1 optical drive found.",
                _ => $"{readings.Count} optical drives found."
            };
            StatusText.Text = readings.Count == 0 ? found : $"{found} Choose a Destination to start a disc.";
        }
    }

    // One button per drive row. What it does follows the row's state:
    // Backup, Cancel, Eject or Log.
    private async void DriveButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpticalDriveRow drive) return;

        switch (drive.State)
        {
            case DriveState.Idle:      await BackupAsync(drive); break;
            case DriveState.BackingUp: drive.CancelBackup();      break;
            case DriveState.Done:      await EjectAsync(drive);  break;
            case DriveState.Failed:    OpenBackupLog(drive);     break;
        }
    }

    // ── Backup ───────────────────────────────────────────────────────

    private async Task BackupAsync(OpticalDriveRow drive)
    {
        // Each refusal says what is wrong. The button's IsEnabled already
        // covers a missing disc, but if the two ever disagree a silent
        // return leaves a button that does nothing at all.
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

        var makeMkv = AppSettings.Instance.MakeMKV_Path;
        if (string.IsNullOrWhiteSpace(makeMkv))
        {
            MessageBox.Show(
                "Please set the MakeMKV path in Preferences.",
                "MakeMKV Path Not Set",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (!File.Exists(makeMkv))
        {
            MessageBox.Show(
                $"MakeMKV was not found at {makeMkv}.",
                "MakeMKV Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // The disc's name is its volume label: it names the ISO, the rip
        // folder and the log, so a label that cannot be a file name has to
        // stop here rather than half way through.
        var name = drive.Label;
        if (name == "(no label)" || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText.Text = $"{drive.Letter} cannot be backed up: \"{name}\" cannot be used as a file name.";
            return;
        }

        var destination = _destination;
        var isoPath     = DiscStore.IsoPath(destination, name);
        var logPath     = DiscStore.BackupLogPath(destination, name);

        // A new backup redoes everything after it, and a running read of
        // this disc would have its ISO pulled out from under it.
        if (FindJob(name, destination) is { IsRunning: true })
        {
            StatusText.Text = $"{name} is being read. Back it up again once the read finishes.";
            return;
        }

        // A disc seen before: asked on what is actually on disk. No stops
        // and nothing is deleted. Yes to Overwrite deletes the old ISO only
        // after the space check below has passed.
        bool overwrite = false;
        if (File.Exists(isoPath))
        {
            if (!Confirm($"{name} already exists. Overwrite?"))
            {
                StatusText.Text = $"{name} left as it was.";
                return;
            }
            overwrite = true;
        }
        else if (File.Exists(logPath))
        {
            if (!Confirm($"{name} has been processed before. Back it up again?"))
            {
                StatusText.Text = $"{name} left as it was.";
                return;
            }
        }

        // Space: the disc's reported size x ReservationFactor must fit in
        // free space now. An ISO about to be overwritten counts as free.
        // Running backups need no adding up: their space is held on disk
        // by their reserve files, so free space already excludes it.
        long needed, free;
        try
        {
            needed = DiskSpace.ReservationFor(new DriveInfo(drive.Letter).TotalSize);
            free   = DiskSpace.FreeBytes(destination);
            if (overwrite) free += new FileInfo(isoPath).Length;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            StatusText.Text = $"{name}: disk space could not be checked: {ex.Message}";
            return;
        }

        if (free < needed)
        {
            MessageBox.Show(
                "Insufficient Disk Space Available",
                "Insufficient Disk Space",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (overwrite)
        {
            try
            {
                File.Delete(isoPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText.Text = $"{name}: the existing ISO could not be removed: {ex.Message}";
                return;
            }
        }

        // A backup of a disc seen before redoes everything after it: its row
        // leaves the jobs list and its record is deleted now, so neither
        // shows the previous run's results while the new backup runs. A
        // row comes back, ready, when the new backup completes.
        if (FindJob(name, destination) is { } oldJob)
            Jobs.Remove(oldJob);
        try
        {
            DiscStore.DeleteRecord(destination, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"{name}: the old record could not be deleted: {ex.Message}";
            return;
        }
        if (destination.Equals(_destination, StringComparison.OrdinalIgnoreCase) &&
            Discs.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } oldDisc)
        {
            if (ReferenceEquals(_selectedDisc, oldDisc)) ShowDiscList();
            Discs.Remove(oldDisc);
        }

        // Claim the space before anything else can: clicks are handled one
        // at a time on this thread, so the next click's check already sees
        // this reservation gone from free space.
        SpaceReservation reservation;
        try
        {
            reservation = SpaceReservation.Create(destination, name, needed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"{name}: space could not be reserved: {ex.Message}";
            return;
        }

        drive.BeginBackup(name, logPath);
        StatusText.Text = $"Backing up {name} from {drive.Letter}.";

        // Progress is created here, on the UI thread, so its callbacks
        // come back to the UI thread and can touch the row's bindings.
        var progress = new Progress<double>(fraction => drive.Progress = fraction);

        using var shrinkStop = new CancellationTokenSource();
        var shrinking = ShrinkWhileRunningAsync(reservation, isoPath, shrinkStop.Token);

        try
        {
            await Task.Run(() => BackupJob.RunAsync(
                makeMkv, drive.Letter, name, isoPath, logPath, progress, drive.BackupToken));

            drive.FinishBackup(DriveState.Done);
            AddOrResetJob(name, drive.Letter, destination);
            StatusText.Text = $"{name} backed up to {isoPath}.";
        }
        catch (OperationCanceledException)
        {
            drive.FinishBackup(DriveState.Idle);
            StatusText.Text = $"{name} backup cancelled.";
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException)
        {
            // Every failure is visible and carries the program's own
            // message rather than a house one that hides it.
            drive.FinishBackup(DriveState.Failed);
            StatusText.Text = $"{name} backup failed: {ex.Message}";
        }
        finally
        {
            // Success, failure or cancel: stop shrinking and close the
            // reserve file, which deletes it and frees what it still held.
            shrinkStop.Cancel();
            try { await shrinking; } catch (OperationCanceledException) { }
            reservation.Dispose();
        }
    }

    // Every couple of seconds, hands back reserved space equal to what the
    // ISO has grown to. A tick that can't read the ISO or resize the
    // reserve is skipped; the next one tries again.
    private static async Task ShrinkWhileRunningAsync(SpaceReservation reservation, string isoPath, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            try
            {
                var iso = new FileInfo(isoPath);
                if (iso.Exists) reservation.Shrink(iso.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Confirm(string message) =>
        MessageBox.Show(message, "Backup", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes;

    private void OpenBackupLog(OpticalDriveRow drive)
    {
        if (drive.LogPath is not { } path || !File.Exists(path))
        {
            StatusText.Text = $"No backup log was written for {drive.BackupDisc}.";
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task EjectAsync(OpticalDriveRow drive)
    {
        try
        {
            await Task.Run(() => OpticalDrive.Eject(drive.Letter));
        }
        catch (Win32Exception ex)
        {
            StatusText.Text = $"{drive.Letter} could not be ejected: {ex.Message}";
            return;
        }

        // The drive reports the change itself; refreshing here as well
        // covers a drive that does not.
        _ = RefreshDrivesAsync();
    }

    // ── Jobs ─────────────────────────────────────────────────────────

    private async void JobAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RipJob job) return;

        if (job.IsRunning)
            job.Cancel();
        else
            await RunAnalysisAsync(job);
    }

    // Rebuilds the jobs list from what is on disk under the Destination:
    // one row per disc whose backup log ends "Backup complete" and whose
    // ISO is still in ISO\. A disc with a record shows its counts; one
    // without shows "ready". Running jobs are left alone, since they are
    // real work in progress; every other row is rebuilt.
    private void RebuildJobs()
    {
        foreach (var job in Jobs.Where(j => !j.IsRunning).ToList())
            Jobs.Remove(job);

        var problems = new List<string>();
        foreach (var log in DiscStore.ReadBackupLogs(_destination, problems))
        {
            if (!log.Completed) continue;
            if (!File.Exists(DiscStore.IsoPath(_destination, log.DiscName))) continue;
            if (FindJob(log.DiscName, _destination) != null) continue;   // running

            var job    = new RipJob(log.DiscName, log.Drive, _destination);
            var record = Discs.FirstOrDefault(d => d.Name.Equals(log.DiscName, StringComparison.OrdinalIgnoreCase));
            if (record != null)
                job.Status = RecordCounts(record);
            Jobs.Add(job);
        }

        if (problems.Count > 0)
        {
            StatusText.Text    = $"{StatusText.Text} Could not read {problems.Count} backup log(s): {string.Join("; ", problems)}";
            StatusText.ToolTip = string.Join("\n", problems);
        }
    }

    // A backup of a disc that already has a row resets that row rather
    // than adding a second one for the same ISO.
    private void AddOrResetJob(string disc, string drive, string destination)
    {
        var existing = FindJob(disc, destination);
        if (existing != null && !existing.IsRunning)
            Jobs.Remove(existing);
        Jobs.Add(new RipJob(disc, drive, destination));
    }

    private RipJob? FindJob(string disc, string destination) =>
        Jobs.FirstOrDefault(j =>
            j.Disc.Equals(disc, StringComparison.OrdinalIgnoreCase) &&
            j.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase));

    private static string RecordCounts(DiscRecord record) =>
        $"{Count(record.Titles.Count, "title")}, {Count(record.Screens.Count, "screen")}";

    // The IFO read, from the disc's backup ISO. The ISO is mounted with
    // no drive letter, read through its volume path, and unmounted again
    // whether the read succeeds, fails or is cancelled. A missing ISO is
    // said so, and the row stays ready for when it is back.
    private async Task RunAnalysisAsync(RipJob job)
    {
        var isoPath = DiscStore.IsoPath(job.Destination, job.Disc);

        if (_busyIsos.Contains(isoPath))
        {
            job.Status = $"{job.Disc} is already being read.";
            return;
        }
        if (!File.Exists(isoPath))
        {
            job.Status = $"{isoPath} was not found.";
            return;
        }

        job.Begin();
        _busyIsos.Add(isoPath);

        // Progress is created here, on the UI thread, so its callbacks
        // come back to the UI thread and can touch the job's bindings.
        var progress = new Progress<string>(message => job.Status = message);
        IsoMount? mount = null;

        try
        {
            // The mount is not cancelled part way: stopping PowerShell in
            // the middle could leave the image mounted. Cancel takes effect
            // as soon as the mount returns.
            job.Status = "mounting ISO";
            mount = await IsoMount.MountAsync(isoPath);
            job.Token.ThrowIfCancellationRequested();

            var record = await Task.Run(() =>
            {
                var disc = DiscAnalysis.ReadDisc(
                    job.Disc, mount.VolumePath,
                    DiscStore.DiscFolder(job.Destination, job.Disc),
                    AppSettings.Instance.FFmpeg_Path,
                    progress, job.Token);

                // The volume path means nothing once the ISO is unmounted;
                // the record names the ISO it was read from instead.
                disc.SourcePath = isoPath;
                DiscStore.Save(job.Destination, disc);
                return disc;
            }, job.Token);

            job.Finish(RecordCounts(record), false);
            if (job.Destination.Equals(_destination, StringComparison.OrdinalIgnoreCase))
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
            if (mount?.Ours == true)
            {
                try
                {
                    await IsoMount.DismountAsync(isoPath);
                }
                catch (IsoMountException ex)
                {
                    // The read's own result stays on the row; the unmount
                    // problem goes to the status line so neither hides the
                    // other.
                    StatusText.Text = $"{job.Disc}: the ISO could not be unmounted: {ex.Message}";
                }
            }
            _busyIsos.Remove(isoPath);
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private sealed record DriveReading(string Letter, string Label, bool HasDisc);

    private static DriveReading ReadDrive(DriveInfo drive)
    {
        var letter = drive.Name.TrimEnd('\\');
        try
        {
            if (!drive.IsReady) return new DriveReading(letter, "No disc", false);
            var label = string.IsNullOrEmpty(drive.VolumeLabel) ? "(no label)" : drive.VolumeLabel;
            return new DriveReading(letter, label, true);
        }
        catch (IOException)
        {
            return new DriveReading(letter, "No disc", false);
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

// Colours for Rip mode's row states. The theme is frozen, so these
// are local rather than theme keys. Both are translucent: laid over
// the row they tint whatever surface is under it, so the theme's own
// text stays legible in the light and dark themes alike.
public static class RipColors
{
    public static readonly Color BackupFill = Color.FromArgb(0x59, 0x43, 0xA0, 0x47);
    public static readonly Color Failed     = Color.FromArgb(0x59, 0xD6, 0x45, 0x45);

    public static readonly SolidColorBrush FailedRow = Frozen(new SolidColorBrush(Failed));

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}

public enum DriveState { Idle, BackingUp, Done, Failed }

// One row in the drives box. The row owns its disc's backup: the
// state, the progress fill and the cancel all live here, and the row
// survives drive refreshes so a backup is never reset by one.
public sealed class OpticalDriveRow : INotifyPropertyChanged
{
    public string Letter { get; }

    public OpticalDriveRow(string letter, string label, bool hasDisc)
    {
        Letter   = letter;
        _label   = label;
        _hasDisc = hasDisc;
    }

    private string _label;
    public string Label
    {
        get => _label;
        private set { if (_label == value) return; _label = value; OnPropertyChanged(); }
    }

    private bool _hasDisc;
    public bool HasDisc
    {
        get => _hasDisc;
        private set { if (_hasDisc == value) return; _hasDisc = value; OnPropertyChanged(); }
    }

    public DriveState State { get; private set; } = DriveState.Idle;

    // The disc the last backup was of, and its log.
    public string  BackupDisc { get; private set; } = "";
    public string? LogPath    { get; private set; }

    private CancellationTokenSource? _backupCancel;
    public CancellationToken BackupToken => _backupCancel?.Token ?? CancellationToken.None;

    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.001) return;
            _progress = value;
            Changed();
        }
    }

    // From a drive refresh. A finished or failed backup belongs to the
    // disc it was of: once that disc is out, the row is ready for the
    // next. A running backup is never touched.
    public void Update(string label, bool hasDisc)
    {
        bool discChanged = hasDisc != HasDisc || label != Label;
        Label   = label;
        HasDisc = hasDisc;

        if (discChanged && State is DriveState.Done or DriveState.Failed)
            State = DriveState.Idle;

        Changed();
    }

    public void BeginBackup(string discName, string logPath)
    {
        BackupDisc    = discName;
        LogPath       = logPath;
        _progress     = 0;
        _backupCancel = new CancellationTokenSource();
        State         = DriveState.BackingUp;
        Changed();
    }

    public void CancelBackup() => _backupCancel?.Cancel();

    public void FinishBackup(DriveState state)
    {
        _backupCancel?.Dispose();
        _backupCancel = null;
        State = state;
        Changed();
    }

    // ── What the view binds to ───────────────────────────────────────

    public string ButtonText => State switch
    {
        DriveState.BackingUp => "Cancel",
        DriveState.Done      => "Eject",
        DriveState.Failed    => "Log",
        _                    => "Backup",
    };

    // Disabled only for a drive with no disc. A missing Destination
    // does not disable it: a disabled button raises no click, so all it
    // could offer is a tooltip, which is too quiet for that mistake.
    public bool CanPress => State != DriveState.Idle || HasDisc;

    public string ButtonHint => State switch
    {
        DriveState.BackingUp => $"Backing up {BackupDisc}: {Progress:P0}. Click to cancel.",
        DriveState.Done      => $"Eject {BackupDisc}.",
        DriveState.Failed    => $"The backup of {BackupDisc} failed. Open its log.",
        _                    => HasDisc ? $"Back up {Label}." : "No disc in this drive.",
    };

    // Green up to the backup's progress while it runs, red after a
    // failure, nothing otherwise.
    public Brush? RowBrush => State switch
    {
        DriveState.BackingUp => ProgressBrush(Progress),
        DriveState.Failed    => RipColors.FailedRow,
        _                    => null,
    };

    public bool HasRowBrush => RowBrush != null;

    // A hard edge at the progress point: the fill colour to the left,
    // clear to the right.
    private static Brush ProgressBrush(double fraction)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop(RipColors.BackupFill, 0));
        brush.GradientStops.Add(new GradientStop(RipColors.BackupFill, fraction));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, fraction));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(CanPress));
        OnPropertyChanged(nameof(ButtonHint));
        OnPropertyChanged(nameof(RowBrush));
        OnPropertyChanged(nameof(HasRowBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Ejects an optical drive's tray. .NET has no call for this, so it
// goes to the drive directly: open the volume, then send it
// IOCTL_STORAGE_EJECT_MEDIA.
internal static class OpticalDrive
{
    private const uint GenericRead           = 0x80000000;
    private const uint FileShareReadWrite    = 0x00000003;
    private const uint OpenExisting          = 3;
    private const uint IoctlStorageEjectMedia = 0x002D4808;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    // Throws Win32Exception with Windows' own message on failure.
    public static void Eject(string driveLetter)
    {
        using var handle = CreateFile(
            $@"\\.\{driveLetter.TrimEnd('\\')}", GenericRead, FileShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (!DeviceIoControl(handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
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
