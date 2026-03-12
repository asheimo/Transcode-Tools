// ============================================================
// Models.cs
// ------------------------------------------------------------
// This file defines the DATA MODELS for the application.
// In C#, a "model" is just a class that holds data — similar
// to a Type or Class in VB.NET.
//
// Each track type (video, audio, subtitle) has its own class
// for both Remux mode and Transcode mode.
// ============================================================

// "using" in C# is the same as "Imports" in VB.NET.
// These bring in the namespaces we need.
using System.ComponentModel;
using System.Runtime.CompilerServices;

// A namespace is like a folder for your code — it keeps class
// names from clashing with other libraries.
namespace TranscodeTools;

// ── Base Class ───────────────────────────────────────────────────────
//
// This is a BASE CLASS that all our track models inherit from.
// Inheritance in C# works the same as in VB.NET — the child class
// gets all the functionality of the parent.
//
// INotifyPropertyChanged is an INTERFACE. Think of it as a contract
// that says "this class promises to fire a PropertyChanged event
// whenever one of its properties changes." WPF uses this to know
// when to refresh the UI automatically.
public class ObservableBase : INotifyPropertyChanged
{
    // This event is part of the INotifyPropertyChanged interface.
    // The ? after the type means it can be null (nullable reference type).
    // In VB.NET you'd write: Public Event PropertyChanged As PropertyChangedEventHandler
    public event PropertyChangedEventHandler? PropertyChanged;

    // This method fires the PropertyChanged event.
    // "protected" means only this class and classes that inherit from it can call it.
    // [CallerMemberName] is an ATTRIBUTE — it automatically fills in the name of the
    // property that called this method, so you don't have to type it manually.
    // The => syntax is a shorthand for a one-line method body (called an expression body).
    // In VB.NET: Protected Sub OnPropertyChanged(name As String)
    //                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
    //            End Sub
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    //                     ^ The ?. is a "null-conditional operator" — it only calls
    //                       .Invoke() if PropertyChanged is not null. Safe shorthand
    //                       for: If PropertyChanged IsNot Nothing Then ...
}


// ════════════════════════════════════════════════════════════
// REMUX MODE MODELS
// ════════════════════════════════════════════════════════════

// ── Remux Video Track ────────────────────────────────────────────────
// Represents one video track row in the Remux video ListView.
public class RemuxVideoTrack : ObservableBase
// ^ The : ObservableBase means this class INHERITS from ObservableBase,
//   just like "Inherits ObservableBase" in VB.NET.
{
    // PRIVATE FIELDS — the actual stored values.
    // The underscore prefix (_) is a C# convention for private fields.
    // "private" means nothing outside this class can access them directly.
    private bool   _isEnabled   = true;  // = true sets the default value
    private string _videoFormat = "";    // "" is an empty string (same as String.Empty)
    private string _resolution  = "";
    private string _fps         = "";
    private bool   _isDefault;           // bool defaults to false if not set

    // PUBLIC PROPERTIES — what the outside world (and WPF bindings) can see.
    // Each property has a getter (get) and setter (set).
    // When the setter is called, we update the private field then call
    // OnPropertyChanged() so WPF knows to refresh the UI.
    //
    // get => _isEnabled        is shorthand for:  get { return _isEnabled; }
    // set { ... }              is the full setter block
    //
    // In VB.NET this would be:
    //   Public Property IsEnabled As Boolean
    //       Get
    //           Return _isEnabled
    //       End Get
    //       Set(value As Boolean)
    //           _isEnabled = value
    //           OnPropertyChanged()
    //       End Set
    //   End Property
    public bool   IsEnabled    { get => _isEnabled;    set { _isEnabled = value;    OnPropertyChanged(); } }
    public string VideoFormat  { get => _videoFormat;  set { _videoFormat = value;  OnPropertyChanged(); } }
    public string Resolution   { get => _resolution;   set { _resolution = value;   OnPropertyChanged(); } }
    public string Fps          { get => _fps;           set { _fps = value;          OnPropertyChanged(); } }
    public bool   IsDefault    { get => _isDefault;    set { _isDefault = value;    OnPropertyChanged(); } }
}

// ── Remux Audio Track ────────────────────────────────────────────────
// Represents one audio track row in the Remux audio ListView.
public class RemuxAudioTrack : ObservableBase
{
    private bool   _isEnabled   = true;
    private string _audioFormat = "";
    private string _width       = "";
    private string _bitRate     = "";
    private string _language    = "";
    private string _title       = "";
    private bool   _isDefault;
    private string _trackId     = "";

    public bool   IsEnabled    { get => _isEnabled;    set { _isEnabled = value;    OnPropertyChanged(); } }
    public string AudioFormat  { get => _audioFormat;  set { _audioFormat = value;  OnPropertyChanged(); } }
    public string Width        { get => _width;        set { _width = value;        OnPropertyChanged(); } }
    public string BitRate      { get => _bitRate;      set { _bitRate = value;      OnPropertyChanged(); } }
    public string Language     { get => _language;     set { _language = value;     OnPropertyChanged(); } }
    public string Title        { get => _title;        set { _title = value;        OnPropertyChanged(); } }
    public bool   IsDefault    { get => _isDefault;    set { _isDefault = value;    OnPropertyChanged(); } }
    public string TrackId      { get => _trackId;      set { _trackId = value;      OnPropertyChanged(); } }
}

// ── Remux Subtitle Track ─────────────────────────────────────────────
// Represents one subtitle track row in the Remux subtitle ListView.
public class RemuxSubtitleTrack : ObservableBase
{
    private bool   _isEnabled      = true;
    private string _subtitleFormat = "";
    private string _language       = "";
    private string _frameCount     = "";
    private string _title          = "";
    private bool   _isDefault;
    private bool   _isForced;
    private string _trackId        = "";

    public bool   IsEnabled       { get => _isEnabled;       set { _isEnabled = value;       OnPropertyChanged(); } }
    public string SubtitleFormat  { get => _subtitleFormat;  set { _subtitleFormat = value;  OnPropertyChanged(); } }
    public string Language        { get => _language;        set { _language = value;        OnPropertyChanged(); } }
    public string FrameCount      { get => _frameCount;      set { _frameCount = value;      OnPropertyChanged(); } }
    public string Title           { get => _title;           set { _title = value;           OnPropertyChanged(); } }
    public bool   IsDefault       { get => _isDefault;       set { _isDefault = value;       OnPropertyChanged(); } }
    public bool   IsForced        { get => _isForced;        set { _isForced = value;        OnPropertyChanged(); } }
    public string TrackId         { get => _trackId;         set { _trackId = value;         OnPropertyChanged(); } }
}


// ════════════════════════════════════════════════════════════
// TRANSCODE MODE MODELS
// ════════════════════════════════════════════════════════════

// ── Transcode Video Track ────────────────────────────────────────────
// Represents one video track row in the Transcode video ListView.
// Note the different columns vs Remux — OutputFormat and FrameRate
// replace the Remux-specific columns.
public class TranscodeVideoTrack : ObservableBase
{
    private bool   _isEnabled    = true;
    private string _trackInfo    = "";
    private string _resolution   = "";
    private string _outputFormat = "";
    private string _frameRate    = "";

    public bool   IsEnabled     { get => _isEnabled;     set { _isEnabled = value;     OnPropertyChanged(); } }
    public string TrackInfo     { get => _trackInfo;     set { _trackInfo = value;     OnPropertyChanged(); } }
    public string Resolution    { get => _resolution;    set { _resolution = value;    OnPropertyChanged(); } }
    public string OutputFormat  { get => _outputFormat;  set { _outputFormat = value;  OnPropertyChanged(); } }
    public string FrameRate     { get => _frameRate;     set { _frameRate = value;     OnPropertyChanged(); } }
}

// ── Transcode Audio Track ────────────────────────────────────────────
public class TranscodeAudioTrack : ObservableBase
{
    private bool   _isEnabled = true;
    private string _trackInfo = "";
    private string _format    = "";
    private string _width     = "";
    private string _bitRate   = "";

    public bool   IsEnabled  { get => _isEnabled;  set { _isEnabled = value;  OnPropertyChanged(); } }
    public string TrackInfo  { get => _trackInfo;  set { _trackInfo = value;  OnPropertyChanged(); } }
    public string Format     { get => _format;     set { _format = value;     OnPropertyChanged(); } }
    public string Width      { get => _width;      set { _width = value;      OnPropertyChanged(); } }
    public string BitRate    { get => _bitRate;    set { _bitRate = value;    OnPropertyChanged(); } }
}

// ── Transcode Subtitle Track ─────────────────────────────────────────
// Note the extra "Burn" property — this is Transcode-only and controls
// whether the subtitle is burned into the video stream.
public class TranscodeSubtitleTrack : ObservableBase
{
    private bool   _isEnabled = true;
    private string _trackInfo = "";
    private bool   _isDefault;
    private bool   _isForced;
    private bool   _burn;

    public bool   IsEnabled  { get => _isEnabled;  set { _isEnabled = value;  OnPropertyChanged(); } }
    public string TrackInfo  { get => _trackInfo;  set { _trackInfo = value;  OnPropertyChanged(); } }
    public bool   IsDefault  { get => _isDefault;  set { _isDefault = value;  OnPropertyChanged(); } }
    public bool   IsForced   { get => _isForced;   set { _isForced = value;   OnPropertyChanged(); } }
    public bool   Burn       { get => _burn;       set { _burn = value;       OnPropertyChanged(); } }
}
