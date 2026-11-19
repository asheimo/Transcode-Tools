// ============================================================
// RipJob.cs
// ------------------------------------------------------------
// One row in the disc list, and the info job behind it.
//
// One row per disc on the Destination: added when a disc's backup
// completes, and rebuilt from disk when a Destination loads (see
// RipView.RebuildJobs). The row shows Ready or Playing, the number
// of titles VLC lists once the disc has been loaded (Tracks), and one
// button, Load Disc / Stop. Step is the disc's next step (Info,
// Naming, then Complete), kept for Hide complete discs.
//
// The info job runs by itself after a backup, and on a Destination
// load for a disc whose info never finished: for a DVD the title table
// (DiscTitles.ReadDvd) and then MakeMKV's info, for a Blu-ray MakeMKV's
// info with mkvmerge's chapter check (MakeMkvInfo). It works from the
// backup, so Drive is only where the disc was backed up from. A job
// outlives the drive it started from, which is why it is its own
// object rather than state on the drive row. It runs off the UI
// thread and reports through IProgress<string>.
// ============================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TranscodeTools;

public sealed class RipJob : INotifyPropertyChanged
{
    private CancellationTokenSource? _cancel;

    public string Disc  { get; }
    public string Drive { get; }

    // The Destination the disc was backed up under. Kept on the job
    // because the view's Destination can be changed while it runs.
    public string Destination { get; }

    public RipJob(string disc, string drive, string destination)
    {
        Disc        = disc;
        Drive       = drive;
        Destination = destination;
    }

    // While the job runs, Info. Otherwise the disc's next step.
    private JobStep _step = JobStep.Info;
    public JobStep Step
    {
        get => _step;
        set { if (_step == value) return; _step = value; OnPropertyChanged(); }
    }

    // What the job is doing now while it runs, and what came of it when
    // it stops. A failure leaves the program's own message here.
    private string _status = "ready";
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShownStatus)); }
    }

    // A failed job's row turns red. The text stays the theme's colour.
    private bool _isError;
    public bool IsError
    {
        get => _isError;
        set { if (_isError == value) return; _isError = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShownStatus)); }
    }

    // Whether this disc is the one in the player.
    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShownStatus));
            OnPropertyChanged(nameof(PlayText));
        }
    }

    // How many titles VLC lists for the disc, set when it is loaded. Null
    // until then.
    private int? _tracks;
    public int? Tracks
    {
        get => _tracks;
        set { if (_tracks == value) return; _tracks = value; OnPropertyChanged(); }
    }

    // The Status column: a running or failed job's own message, otherwise
    // Ready, or Playing while the disc is in the player.
    public string ShownStatus =>
        IsRunning || IsError ? Status
        : IsPlaying          ? "Playing"
        :                      "Ready";

    public string PlayText => IsPlaying ? "Stop" : "Load Disc";

    private JobState _state = JobState.Ready;
    public JobState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(ShownStatus));
        }
    }

    public bool IsRunning => State == JobState.Running;

    public CancellationToken Token => _cancel?.Token ?? CancellationToken.None;

    public void Begin()
    {
        _cancel  = new CancellationTokenSource();
        IsError  = false;
        State    = JobState.Running;
    }

    public void Cancel()
    {
        if (!IsRunning) return;
        Status = "Cancelling...";
        _cancel?.Cancel();
    }

    public void Finish(string status, bool isError)
    {
        Status  = status;
        IsError = isError;
        State   = JobState.Finished;
        _cancel?.Dispose();
        _cancel = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum JobState { Ready, Running, Finished }

// A disc's steps after its backup, in order. Info is the job; Naming is
// done in the title list. Complete: marked complete in the naming step
// (DiscRecord.Complete).
public enum JobStep { Info, Naming, Complete }

// The DVD title table, read from a mounted backup.
public static class DiscTitles
{
    // One title record per entry in VIDEO_TS.IFO's title table. Runs on a
    // background thread: nothing here touches the UI.
    public static List<TitleRecord> ReadDvd(string sourcePath)
    {
        var videoTs = IfoReader.FindVideoTs(sourcePath);
        return Ifo.ReadTitles(IfoReader.ReadVmg(videoTs), IfoReader.VmgFileName)
            .Select(t => new TitleRecord
            {
                Number   = t.Number,
                Vts      = t.Vts,
                VtsTitle = t.VtsTitle,
                Angles   = t.Angles,
                Chapters = t.Chapters,
            })
            .ToList();
    }
}
