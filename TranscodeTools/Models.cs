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

using System.Collections.Generic;
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

    // HDR color metadata — read from ffprobe stream fields.
    // Passed through verbatim to ffmpeg color flags when the source is HDR.
    // e.g. ColorPrimaries = "bt2020", ColorTransfer = "smpte2084", ColorSpace = "bt2020nc"
    // Not shown in the UI.
    public string ColorPrimaries { get; set; } = "";
    public string ColorTransfer  { get; set; } = "";
    public string ColorSpace     { get; set; } = "";

    // Static HDR10 SEI metadata — read from ffprobe side_data_list.
    // MasterDisplay: mastering display color volume (SEI 137), formatted as
    //   G(x,y)B(x,y)R(x,y)WP(x,y)L(max,min) — the exact string ffmpeg expects.
    // MaxCll: content light level (SEI 144), formatted as "MaxCLL,MaxFALL".
    // Both are empty strings for SDR sources or when not present in the stream.
    // Not shown in the UI.
    public string MasterDisplay { get; set; } = "";
    public string MaxCll        { get; set; } = "";

    // HasDoVi is true when the stream contains a Dolby Vision RPU side data record.
    // When true: OutputFormat is locked to "copy (DoVi)", hwaccel/nvenc is bypassed,
    // and the video row is highlighted amber in the UI.
    // Not shown in the UI directly — drives OutputFormat value and row highlight.
    public bool HasDoVi { get; set; } = false;

    // HasHdr10Plus is true when the stream contains HDR10+ dynamic metadata
    // (side_data_type = "HDR Dynamic Metadata"). HDR10+ passthrough is shelved
    // pending an external tool pipeline — this flag is a visual marker only,
    // highlighting the row light blue so files with HDR10+ data can be identified
    // when we're ready to implement it. No functional effect on the encode.
    public bool HasHdr10Plus { get; set; } = false;

    // PresetMismatch is true when the preset restored from a saved settings file
    // differs from AppSettings.Instance.DefaultPreset. Drives amber highlight on
    // the Preset dropdown. Exempt when HasDoVi is true. Not shown directly.
    public bool PresetMismatch { get; set; } = false;

    // QualityFlagsMismatch is true when one or more NVENC quality flag tokens
    // from AppSettings.Instance.NvencQualityFlags are absent from the saved
    // command. Drives the Warning column in the video table. Exempt when HasDoVi.
    public bool QualityFlagsMismatch { get; set; } = false;

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

    // IsLossless is true when the source codec is lossless (TrueHD, DTS-HD MA,
    // DTS:X, FLAC, PCM). Set at probe time by FfprobeService — never changes.
    // Drives: + button visibility, Format/BitRate lock on parent rows.
    // Not shown in the UI directly.
    public bool IsLossless { get; set; } = false;

    // IsSubRow is true for derived lossy tracks spawned from a lossless parent
    // via the + button. Sub-rows share the parent's OriginalTrackIndex as their
    // source but are encoded independently. Not shown in the UI directly —
    // drives row indentation, background color, and - button visibility.
    public bool IsSubRow { get; set; } = false;

    // ParentTrackIndex holds the OriginalTrackIndex of the lossless parent for
    // sub-rows. -1 for normal parent rows. Used by CommandBuilder to map the
    // correct source stream index when building the derived encode command.
    public int ParentTrackIndex { get; set; } = -1;

    // SourceChannels is the raw channel count from ffprobe (e.g. 2, 6, 8).
    // Set at probe time, never changes. Drives AvailableWidths — we never
    // offer a Width option that would require upmixing.
    // For lossless sources this is always populated. For lossy sources ffprobe
    // always reports channels so it should always be > 0.
    public int SourceChannels { get; set; } = 0;

    // SourceBitRateKbps is the source track bitrate in Kbps, read from ffprobe.
    // Set at probe time, never changes. 0 for lossless tracks (no meaningful
    // bitrate to cap against). Drives AvailableBitRates ceiling for lossy sources
    // — we prevent encoding a lossy source at higher than its own bitrate.
    public int SourceBitRateKbps { get; set; } = 0;

    // IsSelected controls whether this track (or sub-row) is included in the
    // output. Defaults to true — all tracks included unless user unchecks.
    // When a parent is deselected, all its sub-rows are also deselected.
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

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
        set
        {
            _format = value;
            OnPropertyChanged();
            // Width options depend on whether we are encoding at all.
            // Keep → only "Keep" is valid for Width (no encoding, no downmix).
            // Any encode format → full width menu based on source channels.
            OnPropertyChanged(nameof(AvailableWidths));
            // Bitrate options also change: Keep format → only "Keep" bitrate.
            OnPropertyChanged(nameof(AvailableBitRates));
            // Reset Width and BitRate if they are no longer valid choices.
            // This prevents a stale "5.1" width sitting on a Keep-format row.
            if (!AvailableWidths.Contains(_width))   Width   = "Keep";
            if (!AvailableBitRates.Contains(_bitRate)) BitRate = "Keep";
        }
    }

    private string _width = "";
    public string Width
    {
        get => _width;
        set
        {
            _width = value;
            OnPropertyChanged();
            // Bitrate ceiling changes with Width: Stereo tops out at 640,
            // 5.1 can go up to 1536. Notify so the BitRate ComboBox rerenders.
            OnPropertyChanged(nameof(AvailableBitRates));
            // Reset BitRate if it is no longer in the new list.
            // e.g. user had 1536 selected for 5.1, then switched to Stereo —
            // 1536 is not valid for Stereo so snap back to "Keep".
            if (!AvailableBitRates.Contains(_bitRate)) BitRate = "Keep";
        }
    }

    private string _bitRate = "";
    public string BitRate
    {
        get => _bitRate;
        set { _bitRate = value; OnPropertyChanged(); }
    }

    // ── Computed option lists ─────────────────────────────────────────
    //
    // These are read-only properties that return the valid options for the
    // Width and BitRate ComboBoxes based on the current track state.
    // WPF re-queries them whenever OnPropertyChanged is called with their name.
    //
    // In VB.NET WinForms you would repopulate a ComboBox manually in an event
    // handler. Here the binding does it — the ComboBox ItemsSource is bound to
    // AvailableWidths/AvailableBitRates, and when PropertyChanged fires for those
    // names WPF calls the getter again and updates the dropdown automatically.

    // AvailableWidths returns the downmix options valid for this track.
    // Rules:
    //   Format = Keep  → only "Keep" (no encoding, downmix flags are irrelevant)
    //   Source ≤ 2ch   → ["Keep", "Stereo"]  (can't offer 5.1, no upmixing)
    //   Source ≥ 6ch   → ["Keep", "5.1", "Stereo"]
    public IReadOnlyList<string> AvailableWidths
    {
        get
        {
            var isKeepFormat = string.IsNullOrEmpty(_format) ||
                               _format.Equals("Keep", StringComparison.OrdinalIgnoreCase);

            if (isKeepFormat)
                return ["Keep"];

            if (SourceChannels >= 6)
                return ["Keep", "5.1", "Stereo"];

            // Stereo or mono source — only downmix option is Stereo (or Keep).
            return ["Keep", "Stereo"];
        }
    }

    // All supported encode bitrates in descending order.
    // 768 is not a standard EAC3/AC3 bitrate and is excluded.
    private static readonly int[] _allBitRates = [1536, 1024, 640, 448, 384, 320, 256, 192];

    // AvailableBitRates returns the valid bitrate choices for this track.
    // Rules:
    //   Format = Keep        → ["Keep"] only (copy, no bitrate applies)
    //   Width = Stereo       → cap at 640 Kbps (stereo ceiling for eac3/ac3)
    //   Width = 5.1 or Keep  → cap at 1536 Kbps
    //   Lossy source         → further cap at SourceBitRateKbps (no upscaling)
    //   Lossless source      → no source-bitrate cap (SourceBitRateKbps = 0)
    public IReadOnlyList<string> AvailableBitRates
    {
        get
        {
            var isKeepFormat = string.IsNullOrEmpty(_format) ||
                               _format.Equals("Keep", StringComparison.OrdinalIgnoreCase);

            if (isKeepFormat)
                return ["Keep"];

            // Width ceiling: Stereo tops out at 640, everything else at 1536.
            var widthCeiling = _width.Equals("Stereo", StringComparison.OrdinalIgnoreCase)
                ? 640 : 1536;

            // Source ceiling: for lossy sources we refuse to encode higher than
            // the source bitrate (lossy-to-lossy quality compounding). Lossless
            // sources have SourceBitRateKbps = 0, which we treat as uncapped.
            var sourceCeiling = (!IsLossless && SourceBitRateKbps > 0)
                ? SourceBitRateKbps : int.MaxValue;

            var effectiveCeiling = Math.Min(widthCeiling, sourceCeiling);

            var list = new List<string> { "Keep" };
            foreach (var br in _allBitRates)
            {
                if (br <= effectiveCeiling)
                    list.Add(br.ToString());
            }
            return list;
        }
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
