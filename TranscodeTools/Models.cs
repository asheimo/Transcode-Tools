// ============================================================
// Models.cs
// ------------------------------------------------------------
// Data models for the six track types across Remux and
// Transcode modes.
//
// Each model class implements INotifyPropertyChanged so that
// when a property value changes, the ListView automatically
// updates to show the new value — no manual UI refresh needed.
//
// This is the C# equivalent of data-bound controls in WPF.
// In VB.NET WinForms you would manually update cell values;
// here the binding does it for you.
// ============================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TranscodeTools;

// ── Base class ───────────────────────────────────────────────────────
//
// All six track models inherit from this base class so they all
// automatically get the INotifyPropertyChanged implementation.
// "abstract" means you cannot create an ObservableBase directly —
// it only exists to be inherited from.
// In VB.NET: Public MustInherit Class ObservableBase
public abstract class ObservableBase : INotifyPropertyChanged
{
    // This event is part of INotifyPropertyChanged.
    // WPF listens to this event to know when to refresh the UI.
    // The ? after the type means the event can be null (no subscribers yet).
    public event PropertyChangedEventHandler? PropertyChanged;

    // Called from property setters to fire the PropertyChanged event.
    // [CallerMemberName] automatically fills in the property name as a string
    // so you don't have to type it manually — e.g. calling OnPropertyChanged()
    // inside the "VideoFormat" setter will pass "VideoFormat" automatically.
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    // ^ ?.Invoke is the null-conditional operator — only calls Invoke if
    //   PropertyChanged is not null (i.e. someone is actually listening).
    //   Equivalent to: If PropertyChanged IsNot Nothing Then PropertyChanged.Invoke(...)
}

// ── REMUX MODELS ─────────────────────────────────────────────────────

public class RemuxVideoTrack : ObservableBase
{
    // OriginalTrackIndex is the stream index as reported by ffprobe (e.g. 0).
    // It is stored internally for building the mkvmerge command but is
    // NOT shown to the user — video is always included automatically.
    public int OriginalTrackIndex { get; set; }

    private string _videoFormat = "";
    public string VideoFormat
    {
        get => _videoFormat;
        set { _videoFormat = value; OnPropertyChanged(); }
    }

    private string _resolution = "";
    public string Resolution
    {
        get => _resolution;
        set { _resolution = value; OnPropertyChanged(); }
    }

    private string _fps = "";
    public string Fps
    {
        get => _fps;
        set { _fps = value; OnPropertyChanged(); }
    }

    private bool _isDefault;
    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; OnPropertyChanged(); }
    }
}

public class RemuxAudioTrack : ObservableBase
{
    // OriginalTrackIndex is the ffprobe stream index (e.g. 1, 2, 3...).
    // Used internally for mkvmerge --audio-tracks and --track-order arguments.
    // Also used to restore drag-reordered rows correctly when reloading settings.
    // NOT shown in the UI.
    public int OriginalTrackIndex { get; set; }

    private string _audioFormat = "";
    public string AudioFormat
    {
        get => _audioFormat;
        set { _audioFormat = value; OnPropertyChanged(); }
    }

    private string _width = "";
    public string Width
    {
        get => _width;
        set { _width = value; OnPropertyChanged(); }
    }

    private string _bitRate = "";
    public string BitRate
    {
        get => _bitRate;
        set { _bitRate = value; OnPropertyChanged(); }
    }

    private string _language = "";
    public string Language
    {
        get => _language;
        set { _language = value; OnPropertyChanged(); }
    }

    private string _title = "";
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    private bool _isDefault;
    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; OnPropertyChanged(); }
    }

    // IsSelected controls whether this track is included in the remux output.
    // In the original app this was row selection in the DataGridView.
    // Here it's an explicit checkbox so the state is unambiguous.
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }
}

public class RemuxSubtitleTrack : ObservableBase
{
    // OriginalTrackIndex — same purpose as in RemuxAudioTrack.
    public int OriginalTrackIndex { get; set; }

    private string _subtitleFormat = "";
    public string SubtitleFormat
    {
        get => _subtitleFormat;
        set { _subtitleFormat = value; OnPropertyChanged(); }
    }

    private string _language = "";
    public string Language
    {
        get => _language;
        set { _language = value; OnPropertyChanged(); }
    }

    private string _frameCount = "";
    public string FrameCount
    {
        get => _frameCount;
        set { _frameCount = value; OnPropertyChanged(); }
    }

    private bool _isDefault;
    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; OnPropertyChanged(); }
    }

    private bool _isForced;
    public bool IsForced
    {
        get => _isForced;
        set { _isForced = value; OnPropertyChanged(); }
    }

    // IsSelected — whether this subtitle track is included in the remux output.
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }
}

// ── TRANSCODE MODELS ─────────────────────────────────────────────────

public class TranscodeVideoTrack : ObservableBase
{
    public int OriginalTrackIndex { get; set; }

    // CodecName stores the raw ffprobe codec_name (e.g. "h264", "hevc", "mpeg2video").
    // Used by BuildTranscodeCommand to select the correct NVDEC hardware decoder.
    // Not shown in the UI.
    public string CodecName { get; set; } = "";

    // PixelFormat stores the raw ffprobe pix_fmt (e.g. "yuv420p", "yuv420p10le").
    // Used by BuildTranscodeCommand to decide whether -highbitdepth and
    // scale_cuda=format=p010le are needed. Not shown in the UI.
    public string PixelFormat { get; set; } = "";

    private string _trackInfo = "";
    public string TrackInfo
    {
        get => _trackInfo;
        set { _trackInfo = value; OnPropertyChanged(); }
    }

    private string _resolution = "";
    public string Resolution
    {
        get => _resolution;
        set { _resolution = value; OnPropertyChanged(); }
    }

    private string _outputFormat = "";
    public string OutputFormat
    {
        get => _outputFormat;
        set { _outputFormat = value; OnPropertyChanged(); }
    }

    private string _frameRate = "";
    public string FrameRate
    {
        get => _frameRate;
        set { _frameRate = value; OnPropertyChanged(); }
    }

    // Preset controls the nvenc quality/speed trade-off.
    // p1 = fastest/lowest quality, p7 = slowest/highest quality.
    // Defaults to p5 — confirmed optimal in pixel-level testing.
    private string _preset = "p5";
    public string Preset
    {
        get => _preset;
        set { _preset = value; OnPropertyChanged(); }
    }
}

public class TranscodeAudioTrack : ObservableBase
{
    public int OriginalTrackIndex { get; set; }

    private string _trackInfo = "";
    public string TrackInfo
    {
        get => _trackInfo;
        set { _trackInfo = value; OnPropertyChanged(); }
    }

    private string _format = "";
    public string Format
    {
        get => _format;
        set { _format = value; OnPropertyChanged(); }
    }

    private string _width = "";
    public string Width
    {
        get => _width;
        set { _width = value; OnPropertyChanged(); }
    }

    private string _bitRate = "";
    public string BitRate
    {
        get => _bitRate;
        set { _bitRate = value; OnPropertyChanged(); }
    }
}

public class TranscodeSubtitleTrack : ObservableBase
{
    public int OriginalTrackIndex { get; set; }

    private string _trackInfo = "";
    public string TrackInfo
    {
        get => _trackInfo;
        set { _trackInfo = value; OnPropertyChanged(); }
    }

    private string _frameCount = "";
    public string FrameCount
    {
        get => _frameCount;
        set { _frameCount = value; OnPropertyChanged(); }
    }

    private bool _isDefault;
    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; OnPropertyChanged(); }
    }

    private bool _isForced;
    public bool IsForced
    {
        get => _isForced;
        set { _isForced = value; OnPropertyChanged(); }
    }

    private bool _burn;
    public bool Burn
    {
        get => _burn;
        set { _burn = value; OnPropertyChanged(); }
    }
}
