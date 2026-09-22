// ============================================================
// RipJob.cs
// ------------------------------------------------------------
// One row in the jobs list, and the work behind it.
//
// A row is added when a disc's backup completes, and waits with a
// Start button until the IFO read is started from it. The read uses
// the backup ISO, so Drive is only where the disc was backed up from.
// The jobs list is also rebuilt from disk when a Destination loads
// (see RipView.RebuildJobs), and every row keeps Start so a read can
// be run again.
//
// A job outlives the drive it started from, which is why it is its
// own object rather than state on the drive row. It runs off the UI
// thread and reports through IProgress<string>; the Python oracle
// printed the same messages to stdout, which would vanish here.
//
// Seam 1 fills the record from the IFOs only: the title table and
// the menu program chains with their cells. Buttons need the VM
// decode to carry a command, so ButtonSets stays empty until seam 2
// rather than being written half-filled -- on disk a half-filled
// button set is indistinguishable from a disc with no menu buttons.
// NavPacks stays 0 for the same reason: counting them means walking
// the cell's VOB sectors, which is the scan that reads the buttons.
// ============================================================

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace TranscodeTools;

public sealed class RipJob : INotifyPropertyChanged
{
    private CancellationTokenSource? _cancel;

    public string Disc  { get; }
    public string Drive { get; }

    // The Destination the disc was backed up under. Kept on the job
    // because the view's Destination can be changed while it waits.
    public string Destination { get; }

    // A job is added once its disc's backup completes and waits,
    // Ready, until its Start is pressed.
    public RipJob(string disc, string drive, string destination)
    {
        Disc        = disc;
        Drive       = drive;
        Destination = destination;
    }

    // The pipeline stage: Backup, Info, Rip or Analyse. Only Analyse
    // runs from the jobs list while seam 1 is the whole engine.
    private string _step = "Analyse";
    public string Step
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
        set { if (_status == value) return; _status = value; OnPropertyChanged(); }
    }

    // A failed job's row turns red. The text stays the theme's colour.
    private bool _isError;
    public bool IsError
    {
        get => _isError;
        set { if (_isError == value) return; _isError = value; OnPropertyChanged(); }
    }

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
            OnPropertyChanged(nameof(ActionText));
        }
    }

    public bool IsRunning => State == JobState.Running;

    // The row's button: Cancel while running, Start otherwise. A finished
    // row keeps Start so its read can be run again.
    public string ActionText => State == JobState.Running ? "Cancel" : "Start";

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

public static class DiscAnalysis
{
    // Reads every IFO on the disc and returns the record they describe.
    // Runs on a background thread: nothing here touches the UI.
    //
    // A single unreadable or malformed IFO does not end the run -- it is
    // reported through progress and the remaining IFOs are read, because
    // one damaged title set should not cost the whole disc. Nothing
    // readable at all is a failure.
    public static DiscRecord ReadDisc(
        string discName,
        string sourcePath,
        IProgress<string> progress,
        CancellationToken token)
    {
        var videoTs = IfoReader.FindVideoTs(sourcePath);
        var ifoPaths = IfoReader.ListIfoFiles(videoTs);

        var record = new DiscRecord
        {
            Name       = discName,
            SourcePath = videoTs,
            CreatedUtc = DateTime.UtcNow,
            // RipFolder is not saved; DiscStore sets it from where the
            // record is stored.
        };

        int read = 0;
        var problems = new List<string>();

        foreach (var path in ifoPaths)
        {
            token.ThrowIfCancellationRequested();

            var name = Path.GetFileName(path);
            progress.Report($"reading {name}");

            Ifo ifo;
            try
            {
                ifo = Ifo.Parse(IfoReader.ReadIfo(path), name);
            }
            catch (Exception ex) when (ex is DiscSourceException or IfoFormatException)
            {
                problems.Add(ex.Message);
                continue;
            }

            try
            {
                // Read into lists first: a malformed offset part way
                // through must leave the record untouched rather than
                // half added.
                var titles  = ifo.Kind == IfoKind.Vmg ? ReadTitles(ifo).ToList() : new List<TitleRecord>();
                var screens = ReadScreens(ifo).ToList();

                record.Titles.AddRange(titles);
                record.Screens.AddRange(screens);
                read++;
            }
            catch (IfoFormatException ex)
            {
                problems.Add(ex.Message);
            }
        }

        if (read == 0)
            throw new DiscSourceException(
                problems.Count == 0
                    ? $"No IFO on {videoTs} could be read."
                    : $"No IFO on {videoTs} could be read: {string.Join("; ", problems)}");

        foreach (var problem in problems)
            progress.Report($"skipped: {problem}");

        return record;
    }

    private static IEnumerable<TitleRecord> ReadTitles(Ifo ifo) =>
        ifo.Titles().Select(t => new TitleRecord
        {
            Number   = t.Number,
            Vts      = t.Vts,
            VtsTitle = t.VtsTitle,
            Angles   = t.Angles,
            Chapters = t.Chapters,
        });

    // One screen per cell of every menu program chain. A PGC with no
    // cells contributes no screen: it is a chain of commands rather than
    // a picture. The resolver reads those from the IFO again at seam 3;
    // they are not lost by being absent here.
    private static IEnumerable<ScreenRecord> ReadScreens(Ifo ifo)
    {
        var domain = ifo.Kind == IfoKind.Vmg ? "vmg" : "vts";

        foreach (var pgc in ifo.MenuPgcs())
            foreach (var cell in pgc.Cells)
                yield return new ScreenRecord
                {
                    Ifo         = ifo.FileName,
                    Domain      = domain,
                    Lang        = pgc.Lang,
                    Menu        = pgc.Menu,
                    Pgc         = pgc.Pgcn,
                    Cell        = cell.Number,
                    Image       = null,
                    FirstSector = cell.FirstSector,
                    LastSector  = cell.LastSector,
                    NavPacks    = 0,
                };
    }
}
