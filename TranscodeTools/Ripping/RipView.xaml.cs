// ============================================================
// RipView.xaml.cs
// ------------------------------------------------------------
// Rip mode's content. The Destination picker and its history, the
// optical drive list, and one list of the Destination's discs (the
// jobs list), each with its next step; the disc records under
// <Destination>\Rip\<DISC>\Menu\ back it. A disc row's Load Disc
// lists its titles below and plays its backup in the player
// (DiscPlayer) in the middle panel; Stop stops it. The player takes the
// arrow keys and Enter while it has focus (click it to give it focus);
// each pick, once the disc is back on its menu, opens a Played box
// with what the disc's record has from the info stage for the titles
// played. Player messages go to player.log in the disc's run log
// folder. The player is created the first time a disc is played,
// closed when Rip mode is left, and disposed when the app closes.
//
// The title list (a DataGrid) is where titles are named. It has one row
// per title MakeMKV's info run joined, from the disc's record
// (TitleNaming.BuildRows): the Name cell edits in place, the Type
// dropdown picks Main title or a Plex extras type, and one Apply button
// above the list saves every changed row to the record. Only named
// titles are ripped.
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
// A completed backup adds a row to the disc list and starts its info
// job (RunInfoJobAsync), which works from the backup, not the drive. A
// DVD's title table is read first: the ISO is mounted with no drive
// letter by IsoMount, VIDEO_TS.IFO is read, the ISO is unmounted. Then
// `makemkvcon info` on the backup (MakeMkvInfo), which for a Blu-ray
// also checks each playlist's chapters with mkvmerge. The record is
// saved when it finishes. A Destination load starts the same job for
// any disc whose info never finished. The disc is not needed after its
// backup, so Eject is safe at any time.
// ============================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TranscodeTools;

public partial class RipView : UserControl
{
    // ── Collections the view binds to ────────────────────────────────
    public ObservableCollection<OpticalDriveRow> Drives { get; } = new();
    public ObservableCollection<RipJob>          Jobs   { get; } = new();
    public ObservableCollection<NamingRow>       TitleRows { get; } = new();

    // The disc records under the Destination. The jobs list shows one row
    // per disc; a selected row's record supplies its titles.
    private readonly List<DiscRecord> _records = new();

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

    // The player, created the first time a disc is played. The disc it is
    // playing, that disc's row (which turns red if the player fails), and
    // the player.log its messages go to.
    private DiscPlayer? _player;
    private DiscRecord? _playerDisc;
    private RipJob?     _playerJob;
    private string?     _playerLogPath;
    private readonly object _playerLogLock = new();

    private const string PlayerLogFileName = "player.log";

    // Whether the player has the keys (its border is green). Cleared only
    // when focus moves to another control in the main window
    // (PlayerFrame_LostKeyboardFocus): a click on VideoView's overlay moves
    // focus into the overlay's own window, and a message box takes it to
    // its own, and neither takes the keys away.
    private bool _playerHasKeys;

    private const string KeyboardOnlyText =
        "The player only takes the keyboard: use the arrow keys to move and Enter to choose.";

    private const string NoDiscPlayingText = "Click Load Disc on a disc to play it.";

    private const string SelectDiscText = "Click Load Disc on a disc above to see its titles.";

    public RipView()
    {
        InitializeComponent();
        DriveList.ItemsSource = Drives;
        JobList.ItemsSource   = Jobs;
        TitleList.ItemsSource = TitleRows;
        ClearTitles(SelectDiscText);

        // Leaving Rip mode stops the disc; closing the app releases VLC.
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClosePlayer(); };
        Dispatcher.ShutdownStarted += (_, _) => _player?.Dispose();

        // Complete discs drop out of view while the box is ticked. Checking
        // the saved choice raises HideComplete_Changed, which refreshes it.
        CollectionViewSource.GetDefaultView(Jobs).Filter = row =>
            !(AppSettings.Instance.RipHideComplete && row is RipJob { Step: JobStep.Complete });
        HideCompleteCheck.IsChecked = AppSettings.Instance.RipHideComplete;
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
        ClearTitles(SelectDiscText);
        _records.Clear();

        var problems = new List<string>();
        foreach (var disc in DiscStore.LoadAll(_destination, problems))
            _records.Add(disc);

        var loaded = DiscCountText();
        StatusText.Text = problems.Count == 0
            ? loaded
            : $"{loaded} Could not read {problems.Count}: {string.Join("; ", problems)}";
        StatusText.ToolTip = problems.Count == 0 ? null : string.Join("\n", problems);
    }

    private string DiscCountText() =>
        _records.Count == 1 ? "1 disc loaded." : $"{_records.Count} discs loaded.";

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

        if (MakeMkvPathOrWarn() is not { } makeMkv) return;

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
        if (DiscStore.BackupExists(isoPath))
        {
            if (!Confirm($"{name} already exists. Overwrite?", "Backup"))
            {
                StatusText.Text = $"{name} left as it was.";
                return;
            }
            overwrite = true;
        }
        else if (File.Exists(logPath))
        {
            if (!Confirm($"{name} has been processed before. Back it up again?", "Backup"))
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
            if (overwrite) free += DiscStore.BackupBytes(isoPath);
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
                DiscStore.DeleteBackup(isoPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText.Text = $"{name}: the existing backup could not be removed: {ex.Message}";
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
            _records.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } oldDisc)
        {
            if (ReferenceEquals(_selectedDisc, oldDisc)) ClearTitles(SelectDiscText);
            _records.Remove(oldDisc);
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
        var progress = new Progress<MakeMkvProgress>(bars => drive.Progress = bars.Total);

        using var shrinkStop = new CancellationTokenSource();
        var shrinking = ShrinkWhileRunningAsync(reservation, () => DiscStore.BackupBytes(isoPath), shrinkStop.Token);

        try
        {
            await Task.Run(() => BackupJob.RunAsync(
                makeMkv, drive.Letter, name, isoPath, logPath, progress, drive.BackupToken));

            drive.FinishBackup(DriveState.Done);
            var job = AddOrResetJob(name, drive.Letter, destination);
            StatusText.Text = $"{name} backed up to {isoPath}.";
            _ = RunInfoJobAsync(job);
        }
        catch (OperationCanceledException)
        {
            drive.FinishBackup(DriveState.Idle);
            StatusText.Text = $"{name} backup cancelled.";
        }
        catch (Exception ex) when (ex is MakeMkvException or IOException or UnauthorizedAccessException)
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
    // job has written so far (`written` measures it: the ISO for a backup,
    // the .mkv files for a rip). A tick that can't read the files or
    // resize the reserve is skipped; the next one tries again.
    private static async Task ShrinkWhileRunningAsync(SpaceReservation reservation, Func<long> written, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            try
            {
                reservation.Shrink(written());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // The MakeMKV path from Preferences, or null after a message box
    // saying what is wrong with it. Checked on Backup only: a job row
    // exists only after a backup that needed the path, and if it has
    // changed since, MakeMKV failing to start says so on the row.
    private static string? MakeMkvPathOrWarn()
    {
        var path = AppSettings.Instance.MakeMKV_Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(
                "Please set the MakeMKV path in Preferences.",
                "MakeMKV Path Not Set",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }
        if (!File.Exists(path))
        {
            MessageBox.Show(
                $"MakeMKV was not found at {path}.",
                "MakeMKV Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }
        return path;
    }

    private static bool Confirm(string message, string caption) =>
        MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question)
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

    // Load Disc lists the disc's titles and plays it; Stop stops it and
    // clears the titles, so the panel never shows a disc that isn't loaded.
    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RipJob job) return;

        if (job.IsPlaying)
        {
            ClearTitles(SelectDiscText);   // also stops the player
            JobList.SelectedItem = null;
        }
        else
        {
            OpenTitles(job);
        }
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RipJob job)
            job.Cancel();
    }

    // Lists the disc's titles below and selects its row, so the row shows
    // which disc is open. Selecting a row by clicking it lists nothing.
    // Also plays the disc's backup in the player.
    private void OpenTitles(RipJob job)
    {
        JobList.SelectedItem = job;
        ShowTitlesFor(job);
        PlayDisc(job);
    }

    // Rebuilds the disc list from what is on disk under the Destination:
    // one row per disc with a record, and one per disc backed up but with
    // no record yet (its backup log ends "Backup complete" and its backup
    // is still in ISO\). A row stays after its backup is deleted. Running
    // jobs are left alone, since they are real work in progress; every
    // other row is rebuilt. A disc whose info never finished (no record,
    // or a record with no title joined) gets its info job started, as it
    // would have after its backup.
    private void RebuildJobs()
    {
        foreach (var job in Jobs.Where(j => !j.IsRunning).ToList())
            Jobs.Remove(job);

        var problems = new List<string>();
        var backups  = DiscStore.ReadBackupLogs(_destination, problems)
                                .Where(l => l.Completed)
                                .ToDictionary(l => l.DiscName, StringComparer.OrdinalIgnoreCase);

        var names = _records.Select(r => r.Name)
                            .Concat(backups.Keys)
                            .Distinct(StringComparer.OrdinalIgnoreCase);
        var unfinished = new List<RipJob>();
        foreach (var name in names)
        {
            if (FindJob(name, _destination) != null) continue;   // running

            var hasBackup = DiscStore.BackupExists(DiscStore.IsoPath(_destination, name));
            var record    = _records.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (record == null && !hasBackup) continue;

            var job = new RipJob(name, backups.GetValueOrDefault(name)?.Drive ?? "", _destination);
            if (record != null)
            {
                job.Status = RecordCounts(record);
                job.Step   = NextStep(record);
            }
            AddJob(job);
            if (job.Step == JobStep.Info && hasBackup) unfinished.Add(job);
        }

        if (problems.Count > 0)
        {
            StatusText.Text    = $"{StatusText.Text} Could not read {problems.Count} backup log(s): {string.Join("; ", problems)}";
            StatusText.ToolTip = string.Join("\n", problems);
        }

        foreach (var job in unfinished)
            _ = RunInfoJobAsync(job);
    }

    // A backup of a disc that already has a row resets that row rather
    // than adding a second one for the same backup.
    private RipJob AddOrResetJob(string disc, string drive, string destination)
    {
        var existing = FindJob(disc, destination);
        if (existing != null && !existing.IsRunning)
            Jobs.Remove(existing);
        var job = new RipJob(disc, drive, destination);
        AddJob(job);
        return job;
    }

    // Rows are kept in disc-name order.
    private void AddJob(RipJob job)
    {
        int at = 0;
        while (at < Jobs.Count && string.Compare(Jobs[at].Disc, job.Disc, StringComparison.OrdinalIgnoreCase) < 0)
            at++;
        Jobs.Insert(at, job);
    }

    // Saved, so the list opens the way it was left. Hidden rows are only
    // filtered out of view.
    private void HideComplete_Changed(object sender, RoutedEventArgs e)
    {
        var hide = HideCompleteCheck.IsChecked == true;
        if (AppSettings.Instance.RipHideComplete != hide)
        {
            AppSettings.Instance.RipHideComplete = hide;
            AppSettings.Instance.Save();
        }
        RefreshJobFilter();
    }

    private void RefreshJobFilter() => CollectionViewSource.GetDefaultView(Jobs).Refresh();

    private RipJob? FindJob(string disc, string destination) =>
        Jobs.FirstOrDefault(j =>
            j.Disc.Equals(disc, StringComparison.OrdinalIgnoreCase) &&
            j.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase));

    // The next step for a disc with a record: Info until MakeMKV's info run
    // has joined a title, then Naming. A disc is Complete only once it has
    // been marked complete (DiscRecord.Complete).
    private static JobStep NextStep(DiscRecord record) =>
        record.Complete                            ? JobStep.Complete
        : record.Titles.Any(t => t.RipName != null) ? JobStep.Naming
        :                                             JobStep.Info;

    // The row's counts: the titles the list shows, and how many are named.
    private static string RecordCounts(DiscRecord record)
    {
        var titles = record.Titles.Where(t => t.RipName != null && t.SameAs == null).ToList();
        var named  = titles.Count(t => t.Name.Length > 0);
        return named == 0 ? Count(titles.Count, "title") : $"{Count(titles.Count, "title")}, {named} named";
    }

    // The info job, from the disc's backup:
    //   DVD      the title table: the ISO is mounted with no drive letter,
    //            VIDEO_TS.IFO is read through its volume path, and the ISO
    //            is unmounted again whether the read succeeds, fails or is
    //            cancelled. Then `makemkvcon info` on the ISO file itself.
    //   Blu-ray  `makemkvcon info` on the BDMV folder, then mkvmerge's
    //            chapter check for each playlist (MakeMkvInfo).
    // The record is created here, so a backup redone starts from nothing,
    // and saved when the job finishes.
    private async Task RunInfoJobAsync(RipJob job)
    {
        var backup = DiscStore.IsoPath(job.Destination, job.Disc);

        if (_busyIsos.Contains(backup))
        {
            job.Status = $"{job.Disc} is already being read.";
            return;
        }
        if (!DiscStore.BackupExists(backup))
        {
            job.Status = $"{backup} was not found.";
            return;
        }
        if (ReferenceEquals(_playerJob, job)) ClosePlayer();

        job.Begin();
        job.Step = JobStep.Info;
        _busyIsos.Add(backup);
        var makeMkv  = AppSettings.Instance.MakeMKV_Path;
        var mkvMerge = AppSettings.Instance.MKVMerge_Path;

        // Created here, on the UI thread, so its callbacks come back to the
        // UI thread and can touch the job's bindings.
        var progress = new Progress<string>(message => job.Status = message);

        try
        {
            var record = new DiscRecord
            {
                Name       = job.Disc,
                SourcePath = backup,
                BluRay     = Directory.Exists(backup),
                CreatedUtc = DateTime.UtcNow,
            };
            if (!record.BluRay)
                record.Titles = await ReadDvdTitlesAsync(job, backup);

            var infoLog = DiscStore.StageLogPath(job.Destination, DateTime.Now, job.Disc, MakeMkvInfo.LogFileName);
            record.RunLogFolder = System.IO.Path.GetDirectoryName(infoLog);

            job.Status = "running MakeMKV info";
            await Task.Run(() => MakeMkvInfo.RunAsync(makeMkv, mkvMerge, backup, infoLog, record, progress, job.Token));
            await Task.Run(() => DiscStore.Save(job.Destination, record));

            job.Step = NextStep(record);
            job.Finish(RecordCounts(record), false);
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
            _busyIsos.Remove(backup);

            // Only this disc's record is reloaded, so names being typed for
            // another disc are kept.
            // The status line's disc count follows, since the job may have
            // just created the disc's record.
            if (job.Destination.Equals(_destination, StringComparison.OrdinalIgnoreCase) &&
                RefreshDiscEntry(job.Disc))
                StatusText.Text = DiscCountText();
        }
    }

    // Reloads one disc's record into the disc list without touching what
    // the view is showing. A record that can no longer be read leaves the
    // list, and the reason goes to the status line. False when a record
    // could not be read.
    private bool RefreshDiscEntry(string discName)
    {
        var problems = new List<string>();
        var fresh = DiscStore.LoadAll(_destination, problems)
                             .FirstOrDefault(d => d.Name.Equals(discName, StringComparison.OrdinalIgnoreCase));
        if (problems.Count > 0)
            StatusText.Text = $"Could not read {problems.Count} record(s): {string.Join("; ", problems)}";

        var at = _records.FindIndex(d => d.Name.Equals(discName, StringComparison.OrdinalIgnoreCase));
        if (fresh == null)
        {
            if (at >= 0) _records.RemoveAt(at);
            return problems.Count == 0;
        }
        if (at >= 0)
        {
            _records[at] = fresh;
            return problems.Count == 0;
        }

        at = 0;
        while (at < _records.Count && string.Compare(_records[at].Name, fresh.Name, StringComparison.OrdinalIgnoreCase) < 0)
            at++;
        _records.Insert(at, fresh);
        return problems.Count == 0;
    }

    // Mounts the DVD's ISO, reads its title table, and unmounts it again
    // before returning, so nothing after this holds the mount.
    private async Task<List<TitleRecord>> ReadDvdTitlesAsync(RipJob job, string isoPath)
    {
        IsoMount? mount = null;
        try
        {
            // The mount is not cancelled part way: stopping PowerShell in
            // the middle could leave the image mounted. Cancel takes effect
            // as soon as the mount returns.
            job.Status = "mounting ISO";
            mount = await IsoMount.MountAsync(isoPath);
            job.Token.ThrowIfCancellationRequested();

            job.Status = "reading the title table";
            return await Task.Run(() => DiscTitles.ReadDvd(mount.VolumePath), job.Token);
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
                    // The job's own result stays on the row; the unmount
                    // problem goes to the status line so neither hides the
                    // other.
                    StatusText.Text = $"{job.Disc}: the ISO could not be unmounted: {ex.Message}";
                }
            }
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

    // ── Titles of the selected disc ────────────────────────────────

    // A disc with a record lists its titles; one not yet analysed has none.
    private void ShowTitlesFor(RipJob? job)
    {
        if (job == null)
        {
            ClearTitles(SelectDiscText);
            return;
        }

        var disc = _records.FirstOrDefault(d => d.Name.Equals(job.Disc, StringComparison.OrdinalIgnoreCase));
        if (disc == null)
        {
            ClearTitles($"{job.Disc} has no titles until its info run has finished.");
            return;
        }
        ShowDiscTitles(disc);
    }

    // Opens on the first title, so its menu screen shows straight away.
    private void ShowDiscTitles(DiscRecord disc)
    {
        _selectedDisc = disc;
        SelectedDiscName.Text = disc.Name;

        TitleRows.Clear();
        try
        {
            var shownExtras = TitleType.ShownExtras(AppSettings.Instance.RipHiddenExtraTypes);
            foreach (var row in TitleNaming.BuildRows(disc, shownExtras))
            {
                row.PropertyChanged += NamingRow_PropertyChanged;
                TitleRows.Add(row);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"{disc.Name}: the rip folder could not be read: {ex.Message}";
        }

        DiscTitlesHeader.Visibility  = Visibility.Visible;
        TitlesPlaceholder.Visibility = Visibility.Collapsed;
        TitleList.Visibility         = Visibility.Visible;
        TitleList.IsReadOnly         = disc.Complete;
        TitleList.SelectedIndex     = TitleRows.Count > 0 ? 0 : -1;
        UpdatePanelButtons();
    }

    // No disc's titles: the list area says why, and the player stops.
    private void ClearTitles(string message)
    {
        _selectedDisc = null;
        TitleRows.Clear();
        ClosePlayer();

        DiscTitlesHeader.Visibility  = Visibility.Collapsed;
        TitleList.Visibility         = Visibility.Collapsed;
        TitlesPlaceholder.Text       = message;
        TitlesPlaceholder.Visibility = Visibility.Visible;
    }

    // Right-click on a disc row: open its rip folder. The menu item's
    // DataContext is the row's RipJob, inherited from the row.
    private void OpenDiscFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RipJob job) return;

        var folder = DiscStore.RipFolder(job.Destination, job.Disc);
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(
                $"The rip folder for {job.Disc} was not found:\n{folder}",
                "Folder Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        Process.Start("explorer.exe", $"\"{folder}\"");
    }

    // ── Naming ───────────────────────────────────────────────────────

    // Clicking into a row's Type box selects the row, so the row being
    // worked on is the one highlighted.
    private void TitleRow_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is DataGridRow row)
            row.IsSelected = true;
    }

    // A click in a Name cell selects its row and starts the edit straight
    // away; the DataGrid's own rule is a second click. The click is not
    // handled here, so it carries on into the text box and places the caret.
    private void NameCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell { IsEditing: false } cell) return;

        cell.Focus();
        DataGridRow.GetRowContainingElement(cell).IsSelected = true;
        TitleList.BeginEdit(e);
    }

    // A name being typed only reaches its row when the edit ends (Enter, or
    // leaving the cell), which is what lets Esc undo it. So Apply is enabled
    // for the whole edit; Apply ends the edit first and then looks at the
    // rows. When an edit ends any other way, the button is set again once the
    // grid has written the value back.
    private void TitleList_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        ApplyButton.IsEnabled    = true;
        CompleteButton.IsEnabled = false;
    }

    private void TitleList_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.InvokeAsync(UpdatePanelButtons);

    private void NamingRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NamingRow.HasChanges))
            UpdatePanelButtons();
    }

    // Naming: Apply while there are changes, Complete while there are none.
    // A complete disc: Edit, and Complete disabled.
    private void UpdatePanelButtons()
    {
        var complete = _selectedDisc?.Complete == true;
        var changes  = TitleRows.Any(r => r.HasChanges);

        ApplyButton.Content      = complete ? "Edit" : "Apply";
        ApplyButton.IsEnabled    = complete || changes;
        CompleteButton.IsEnabled = !complete && !changes;
    }

    // Saves every changed row's name and type to the disc's record, the way
    // Remux and Transcode save their settings file. Every changed row is
    // checked first, and the names across all rows; any problem stops the
    // whole Apply and lists them. One confirm covers the lot.
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisc is not { } disc) return;

        // A running info job rewrites this disc's record under the rows.
        if (FindJob(disc.Name, _destination) is { IsRunning: true })
        {
            StatusText.Text = $"{disc.Name} is being read. Apply once it finishes.";
            return;
        }

        // On a complete disc the button reads Edit and reopens the disc.
        if (disc.Complete)
        {
            EditDisc(disc);
            return;
        }

        TitleList.CommitEdit(DataGridEditingUnit.Row, true);

        var changed = TitleRows.Where(r => r.HasChanges).ToList();
        if (changed.Count == 0) return;

        var problems = changed
            .Select(r => (Row: r, Problem: TitleNaming.Problem(r)))
            .Where(x => x.Problem != null)
            .Select(x => $"Title {x.Row.Number}: {x.Problem}")
            .Concat(TitleNaming.Duplicates(TitleRows))
            .ToList();
        if (problems.Count > 0)
        {
            MessageBox.Show(string.Join("\n", problems), "Cannot Apply", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var message = $"Save {Count(changed.Count, "name")}?";
        var untagged = changed
            .Where(r => r.Type == TitleType.MainTitle && !TitleNaming.LooksPlexReady(TitleNaming.StripExtension(r.Name)))
            .ToList();
        if (untagged.Count > 0)
            message += $"\n\nNo year or episode number in: {string.Join(", ", untagged.Select(r => TitleNaming.StripExtension(r.Name)))}";
        if (!Confirm(message, "Apply")) return;

        try
        {
            foreach (var row in changed)
                TitleNaming.Apply(row, disc, _destination);
            DiscStore.Save(_destination, disc);
            StatusText.Text = $"Saved {Count(changed.Count, "name")}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Apply Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateJobRow(disc);
        UpdatePanelButtons();
    }

    // After naming, the disc's row shows its new counts and next step. A
    // disc just marked complete drops out of view while complete discs are
    // hidden; its titles stay listed below.
    private void UpdateJobRow(DiscRecord disc)
    {
        if (FindJob(disc.Name, _destination) is not { } job) return;
        job.Status = RecordCounts(disc);
        job.Step   = NextStep(disc);
        RefreshJobFilter();
    }

    // Marks the disc complete. Every named title must have been ripped
    // first. With the "when the disc is marked complete" setting and the
    // ISO still there,
    // the ISO is deleted too, after Confirm; Cancel leaves everything as it
    // was. The ISO goes before the mark, so a delete that fails changes
    // nothing. The row stays.
    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisc is not { } disc) return;

        if (FindJob(disc.Name, _destination) is { IsRunning: true })
        {
            StatusText.Text = $"{disc.Name} is being read. Mark it complete once it finishes.";
            return;
        }

        var notRipped = TitleNaming.NamedNotRipped(disc);
        if (notRipped.Count > 0)
        {
            MessageBox.Show(
                $"These named titles have not been ripped yet:\n{string.Join("\n", notRipped.Select(t => $"Title {t.Number}: {t.Name}"))}",
                "Cannot Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var iso       = DiscStore.IsoPath(_destination, disc.Name);
        var deleteIso = AppSettings.Instance.IsoDeleteAfter == IsoDeleteAfter.Naming && DiscStore.BackupExists(iso);
        if (deleteIso &&
            !ConfirmDialog.Show(Window.GetWindow(this), $"Mark {disc.Name} complete? Its ISO will be deleted.", "Complete"))
            return;

        try
        {
            if (deleteIso)
            {
                if (ReferenceEquals(_playerDisc, disc)) ClosePlayer();
                DiscStore.DeleteBackup(iso);
                TitleNaming.Log(disc, _destination, $"ISO deleted: {iso}");
            }
            disc.Complete = true;
            DiscStore.Save(_destination, disc);
            TitleNaming.Log(disc, _destination, "marked complete");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Complete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        TitleList.IsReadOnly = true;
        StatusText.Text = deleteIso ? $"{disc.Name} is complete; its ISO was deleted." : $"{disc.Name} is complete.";
        UpdateJobRow(disc);
        UpdatePanelButtons();
    }

    // Edit on a complete disc: back to Naming, with the titles editable.
    private void EditDisc(DiscRecord disc)
    {
        try
        {
            disc.Complete = false;
            DiscStore.Save(_destination, disc);
            TitleNaming.Log(disc, _destination, "reopened for editing");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            disc.Complete = true;
            MessageBox.Show(ex.Message, "Edit Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        TitleList.IsReadOnly = false;
        StatusText.Text = $"{disc.Name} is open for editing.";
        UpdateJobRow(disc);
        UpdatePanelButtons();
    }

    // ── Player ───────────────────────────────────────────────────────

    // Plays the disc's backup: an .iso for a DVD, a BDMV folder for a
    // Blu-ray. Whatever was playing is closed first. A disc without a
    // record or a backup says so in the player panel.
    private void PlayDisc(RipJob job)
    {
        ClosePlayer();

        var disc = _records.FirstOrDefault(d => d.Name.Equals(job.Disc, StringComparison.OrdinalIgnoreCase));
        if (disc == null) return;

        var backup = DiscStore.IsoPath(job.Destination, job.Disc);
        if (!DiscStore.BackupExists(backup))
        {
            ShowPlayerPlaceholder($"{job.Disc} has no backup to play: {backup} was not found.");
            return;
        }

        try
        {
            _player ??= CreatePlayer();
        }
        catch (Exception ex) when (ex is InvalidOperationException or LibVLCSharp.Shared.VLCException)
        {
            ShowPlayerPlaceholder($"The player could not start: {ex.Message}");
            return;
        }

        _playerDisc    = disc;
        _playerJob     = job;
        _playerLogPath = PlayerLogPath(disc, job.Destination);
        WritePlayerLog($"opened {backup}");

        // The cover goes black and stays up until VLC is playing
        // (OnPlayerStarted): before then the panel's window is empty. The
        // frame behind the video goes black too: the panel's native window
        // paints no background of its own, so the frame shows through
        // wherever VLC draws nothing (letterbox bands, start-up). Black from
        // here on matches VLC's own picture, so nothing changes colour.
        ShowPlayerPlaceholder($"Loading {job.Disc}...");
        PlayerCover.Background = Brushes.Black;
        PlayerFrame.Background = Brushes.Black;
        job.IsPlaying = true;
        _player.Open(backup);
        PlayerFrame.Focus();
        _playerHasKeys = true;
    }

    private DiscPlayer CreatePlayer()
    {
        var player = new DiscPlayer();
        player.Message      += (level, text) => WritePlayerLog($"{level,-5} {text}");
        player.BackOnMenu   += ShowPlayed;
        player.Failed       += PlayerFailed;
        player.TitlesListed += OnPlayerStarted;
        PlayerVideo.MediaPlayer = player.MediaPlayer;
        return player;
    }

    // VLC lists the disc's titles once it is playing (DiscPlayer raises
    // TitlesListed from VLC's Playing event), so this is the point where its
    // video window exists. VLC opens that window at the video's own size and
    // only fills the panel when the panel is next resized, which a second
    // disc in the same panel never gets; until then the panel's window shows
    // around it. So it is sized to the panel here, then uncovered.
    private void OnPlayerStarted(int titles)
    {
        if (_playerJob == null) return;
        _playerJob.Tracks = titles;
        if (_player != null) VideoWindow.FillParent(_player.MediaPlayer.Hwnd);
        PlayerCover.Visibility = Visibility.Collapsed;
    }

    // Stops the disc and empties the panel. Safe with nothing playing.
    private void ClosePlayer()
    {
        if (_playerDisc != null) WritePlayerLog("closed");
        _player?.Close();
        _playerHasKeys = false;
        if (_playerJob != null) _playerJob.IsPlaying = false;
        _playerDisc    = null;
        _playerJob     = null;
        _playerLogPath = null;
        ShowPlayerPlaceholder(NoDiscPlayingText);
    }

    // The cover and the frame in the panel colour, with a message. PlayDisc
    // turns both black while a disc is loaded.
    private void ShowPlayerPlaceholder(string text)
    {
        PlayerPlaceholder.Text = text;
        PlayerCover.SetResourceReference(Border.BackgroundProperty, "PanelBg");
        PlayerFrame.SetResourceReference(Border.BackgroundProperty, "PanelBg");
        PlayerCover.Visibility = Visibility.Visible;
    }

    // player.log in the disc's run log folder, beside info.log and rip.log.
    // A record from before that folder was recorded gets a new run folder,
    // kept on the record, as naming.log does.
    private static string PlayerLogPath(DiscRecord disc, string destination)
    {
        if (string.IsNullOrEmpty(disc.RunLogFolder))
        {
            disc.RunLogFolder = System.IO.Path.GetDirectoryName(
                DiscStore.StageLogPath(destination, DateTime.Now, disc.Name, PlayerLogFileName))!;
            DiscStore.Save(destination, disc);
        }
        Directory.CreateDirectory(disc.RunLogFolder);
        return System.IO.Path.Combine(disc.RunLogFolder, PlayerLogFileName);
    }

    // The Blu-ray log reader reports from its own thread, hence the lock.
    private void WritePlayerLog(string line)
    {
        lock (_playerLogLock)
        {
            if (_playerLogPath == null) return;
            File.AppendAllText(_playerLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
        }
    }

    // Click the player to give it the keys. The click lands on the overlay
    // VideoView keeps above VLC's own window, so both it and the frame
    // around it hand focus to the frame.
    // A click while the player already has the keys does nothing on the
    // disc (mouse input is off), so it says how to drive it instead.
    private void Player_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Window.GetWindow(this)?.Activate();
        PlayerFrame.Focus();

        if (_playerDisc == null) return;
        if (_playerHasKeys)
            MessageBox.Show(KeyboardOnlyText, "Player", MessageBoxButton.OK, MessageBoxImage.Information);
        _playerHasKeys = true;
    }

    // The keys go only when focus lands on another control in the main
    // window; focus leaving the window (overlay, message box, another app)
    // keeps them.
    private void PlayerFrame_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var inMainWindow = e.NewFocus is DependencyObject target &&
                           ReferenceEquals(Window.GetWindow(target), Window.GetWindow(this));
        if (inMainWindow) _playerHasKeys = false;
    }

    // Arrows and Enter drive the disc's menus. Enter is the only way to
    // start a title, so it is what marks a pick.
    private void Player_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_player == null || _playerDisc == null) return;

        LibVLCSharp.Shared.NavigationMode? mode = e.Key switch
        {
            Key.Up    => LibVLCSharp.Shared.NavigationMode.Up,
            Key.Down  => LibVLCSharp.Shared.NavigationMode.Down,
            Key.Left  => LibVLCSharp.Shared.NavigationMode.Left,
            Key.Right => LibVLCSharp.Shared.NavigationMode.Right,
            Key.Enter => LibVLCSharp.Shared.NavigationMode.Activate,
            _         => null,
        };
        if (mode is null) return;

        _player.Navigate(mode.Value);
        e.Handled = true;
    }

    // A pick, once the disc is back on its menu, and what the record has for
    // it. DVD: each title played, by number. Blu-ray: the pick's playlist,
    // and when the start was ambiguous each candidate, each with the title
    // MakeMKV built from it or "not listed by MakeMKV". When that comes to
    // exactly one row, the row is selected (the kept title's row for a copy
    // MakeMKV skipped); the keys stay with the player.
    private void ShowPlayed(PickResult result)
    {
        if (_playerDisc is not { } disc) return;

        List<string> lines;
        List<TitleRecord> matches;
        if (result.DiscType == DiscType.BluRay)
        {
            var playlists = result.Start is { Candidates.Count: > 1 } start
                ? start.Candidates.Select(c => c.Playlist).ToList()
                : result.Playlist is { } playlist ? new List<string> { playlist } : new List<string>();
            var found = playlists.Select(p => (Playlist: p, Title: TitleForPlaylist(disc, p))).ToList();
            lines = found.Select(f => $"Playlist {f.Playlist}  \u2192  " +
                                      (f.Title is { } t ? $"title {t.Number}  {t.Duration}" : "not listed by MakeMKV"))
                         .ToList();
            if (playlists.Count > 1) lines.Insert(0, "Ambiguous start:");
            matches = found.Where(f => f.Title != null).Select(f => f.Title!).ToList();
        }
        else
        {
            lines = result.Played
                .Select(p => $"Title {p.Number}  {p.Length:hh\\:mm\\:ss}  \u2192  {Describe(disc, p.Number)}")
                .ToList();
            matches = result.Played.Count == 1 && RowTitle(disc, result.Played[0].Number) is { } only
                ? new List<TitleRecord> { only }
                : new List<TitleRecord>();
        }

        if (matches.Count == 1 && ReferenceEquals(_selectedDisc, disc) &&
            TitleRows.FirstOrDefault(r => r.Title == matches[0]) is { } row)
        {
            TitleList.SelectedItem = row;
            TitleList.ScrollIntoView(row);
        }

        var text = string.Join(Environment.NewLine, lines);
        WritePlayerLog($"Played:{Environment.NewLine}{text}");
        MessageBox.Show(text, "Played");
    }

    // The title MakeMKV built from a Blu-ray playlist ("00800", as the
    // player names it), or null.
    private static TitleRecord? TitleForPlaylist(DiscRecord disc, string playlist) =>
        disc.Titles.FirstOrDefault(t => string.Equals(t.Playlist, playlist + ".mpls", StringComparison.OrdinalIgnoreCase));

    // The title whose row a DVD title number lands on: its own, or the kept
    // title's for a copy MakeMKV skipped. Null when it has no row.
    private static TitleRecord? RowTitle(DiscRecord disc, int number)
    {
        var title = disc.Titles.FirstOrDefault(t => t.Number == number);
        if (title?.SameAs is int kept)
            title = disc.Titles.FirstOrDefault(t => t.Number == kept);
        return title is { RipName: not null } ? title : null;
    }

    // A DVD title number as the record has it: its name or MakeMKV's name,
    // or why there is none.
    private static string Describe(DiscRecord disc, int number)
    {
        var title = disc.Titles.FirstOrDefault(t => t.Number == number);
        if (title == null)
            return "not in the disc's title table";
        var prefix = "";
        if (title.SameAs is int kept)
        {
            prefix = $"same as title {kept}: ";
            title  = disc.Titles.FirstOrDefault(t => t.Number == kept);
            if (title == null) return prefix + "not in the disc's title table";
        }
        if (title.RipName == null)
            return prefix + "MakeMKV didn't list it";
        return prefix + (title.Name.Length > 0 ? $"\"{title.Name}\"" : title.RipName);
    }

    // Pick tracking stopped: the disc's row turns red with the error, as
    // any other failure in the jobs list does.
    private void PlayerFailed(Exception ex)
    {
        WritePlayerLog($"failed: {ex}");
        if (_playerJob is not { } job) return;
        job.Status  = $"Player: {ex.Message}";
        job.IsError = true;
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

// How the menu picture uses its space: fill the width and scroll, or
// shrink or grow to show it whole.
public enum PictureFit { Width, Window }

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

    // The backup's progress as text, in its own column, so it shows while
    // the green fill is behind the row's button. Empty when not backing up.
    public string Percent => State == DriveState.BackingUp ? $"{Progress:P0}" : "";

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
        OnPropertyChanged(nameof(Percent));
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

// Sizes the windows VLC puts inside the player's panel window to fill it.
// VLC lays out its video inside its own window whenever that window is
// resized, so sizing it is enough.
internal static class VideoWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    private const uint SwpNoZOrder   = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // `panel` is the window handle VLC was given. Only its direct children
    // are sized; VLC places the windows inside those itself.
    public static void FillParent(IntPtr panel)
    {
        if (panel == IntPtr.Zero || !GetClientRect(panel, out var client)) return;

        var children = new List<IntPtr>();
        EnumChildWindows(panel, (hwnd, _) =>
        {
            if (GetParent(hwnd) == panel) children.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        foreach (var child in children)
            SetWindowPos(child, IntPtr.Zero, 0, 0, client.Right, client.Bottom, SwpNoZOrder | SwpNoActivate);
    }
}
