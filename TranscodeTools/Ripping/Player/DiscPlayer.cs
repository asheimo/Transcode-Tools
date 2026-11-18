// ============================================================
// DiscPlayer.cs
// ------------------------------------------------------------
// Plays a disc backup with libVLC and reports which title the user
// picked once the disc is back on its menu. What both disc types
// share lives here (VLC, keys, menu commands, title list, logging);
// how a pick is decided lives in DvdPicks or BluRayPicks, chosen at
// Open. Close ends the disc so another can be opened on the same
// player.
// ============================================================

using System.IO;
using LibVLCSharp.Shared;

namespace TranscodeTools;

public enum MessageLevel { Info, Debug }
public enum DiscType { Dvd, BluRay }

public sealed class DiscPlayer : IDisposable
{
    private readonly LibVLC _libVLC;
    private readonly SynchronizationContext _ui;
    private readonly string? _debugLogPath;
    private readonly object _logLock = new();
    private readonly BdLogReader _bdLog;
    private readonly Dictionary<int, string> _titleNames = new();
    private IDiscPicks? _picks;
    private int _timeTickPending; // 1 while a time tick is queued on the UI thread

    public MediaPlayer MediaPlayer { get; }
    public DiscType DiscType { get; private set; }

    public event Action<PickResult>? BackOnMenu;
    public event Action<MessageLevel, string>? Message;
    // Pick tracking stopped on an error it can't recover from (BdLogReader); raised on the UI thread
    public event Action<Exception>? Failed;

    public DiscPlayer(string? debugLogPath = null)
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("DiscPlayer must be created on the UI Thread.");
        _debugLogPath = debugLogPath;
        _bdLog = new BdLogReader(Path.Combine(AppContext.BaseDirectory, "bd_debug.log"), SendMessage);
        _bdLog.Failed += ex => Failed?.Invoke(ex);

        Core.Initialize(FindLibVlcFolder());
        _libVLC = new LibVLC(enableDebugLogs: debugLogPath != null, "--no-audio");
        if (debugLogPath != null)
        {
            File.Delete(debugLogPath);
            _libVLC.Log += LibVLC_Log;
        }
        MediaPlayer = new MediaPlayer(_libVLC)
        {
            EnableMouseInput = false // every disc is driven by keys, so Enter is the one way to start a title
        };
        MediaPlayer.Playing += MediaPlayer_Playing;
        MediaPlayer.TitleChanged += MediaPlayer_TitleChanged;
        MediaPlayer.ChapterChanged += MediaPlayer_ChapterChanged;
        MediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
    }

    public void Open(string isoPath)
    {
        // MakeMKV writes a Blu-ray backup as a BDMV folder and a DVD backup as an .iso file
        DiscType = Directory.Exists(Path.Combine(isoPath, "BDMV")) ? DiscType.BluRay : DiscType.Dvd;
        if (DiscType == DiscType.BluRay)
        {
            _picks = new BluRayPicks(isoPath, MediaPlayer, SendMessage, _bdLog, TopMenu);
            _bdLog.Start();
        }
        else
        {
            _picks = new DvdPicks(MediaPlayer, SendMessage);
        }
        _picks.BackOnMenu += result => BackOnMenu?.Invoke(WithNames(result));

        var scheme = DiscType == DiscType.BluRay ? "bluray:///" : "dvd:///";
        var mrl = scheme + isoPath.Replace('\\', '/');
        using var media = new Media(_libVLC, mrl, FromType.FromLocation);
        MediaPlayer.Play(media);
    }

    // Ends the open disc: playback stops, the Blu-ray log reader stops and the disc's pick tracking is
    // dropped, so the next Open starts clean. Safe to call with no disc open.
    public void Close()
    {
        MediaPlayer.Stop();
        _bdLog.Stop();
        _picks?.Dispose();
        _picks = null;
        _titleNames.Clear();
    }

    // Skips the playing Blu-ray playlist to 1 s before its end. No key is mapped to it yet.
    public void SkipPlaylist()
    {
        if (_picks is null)
        {
            SendMessage(MessageLevel.Info, "skip: no playlist playing");
            return;
        }
        _picks.SkipPlaylist();
    }

    // The disc's Top Menu (VLC title 0 calls libbluray's bd_menu_call). The Blu-ray start-up skip
    // uses it; no key is mapped to it yet.
    public void TopMenu()
    {
        SendMessage(MessageLevel.Info, "top menu");
        MediaPlayer.Title = 0;
    }

    public void Navigate(NavigationMode mode)
    {
        SendMessage(MessageLevel.Debug, $"navigate {mode}");
        if (mode == NavigationMode.Activate)
            _picks?.OnEnter();
        MediaPlayer.Navigate((uint)mode);
    }

    public void Dispose()
    {
        _picks?.Dispose();
        _bdLog.Dispose();
        MediaPlayer.Dispose();
        _libVLC.Dispose();
    }

    private void LibVLC_Log(object? sender, LogEventArgs e)
    {
        lock (_logLock)
        {
            File.AppendAllText(_debugLogPath!, $"{DateTime.Now:HH:mm:ss.fff} {e.Level}: {e.Message}{Environment.NewLine}");
        }
    }

    private void MediaPlayer_Playing(object? sender, EventArgs e)
    {
        _ui.Post(_ => ListTitles(), null);
    }

    private void ListTitles()
    {
        var titles = MediaPlayer.TitleDescription;
        SendMessage(MessageLevel.Info, $"{titles.Length} titles");

        foreach (var title in titles)
        {
            var chapters = MediaPlayer.FullChapterDescriptions(title.Id);
            var length = chapters.Sum(c => c.Duration);
            _titleNames[title.Id] = title.Name ?? "";
            var duration = TimeSpan.FromMilliseconds(length);
            SendMessage(MessageLevel.Info, $"{title.Id}: {duration:hh\\:mm\\:ss} {chapters.Length} ch {title.Name}");
        }
    }

    // The picks classes don't see VLC's title list, so the names are added here
    private PickResult WithNames(PickResult result) => result with
    {
        Played = result.Played.Select(t => t with { Name = _titleNames.GetValueOrDefault(t.Number, "") }).ToList()
    };

    private void MediaPlayer_TitleChanged(object? sender, MediaPlayerTitleChangedEventArgs e)
    {
        var title = e.Title;
        _ui.Post(_ => OnTitle(title), null);
    }

    private void OnTitle(int title)
    {
        SendMessage(MessageLevel.Debug, $"title -> {title}");
        _picks?.OnTitle(title);
    }

    private void MediaPlayer_ChapterChanged(object? sender, MediaPlayerChapterChangedEventArgs e)
    {
        var chapter = e.Chapter;
        _ui.Post(_ => SendMessage(MessageLevel.Debug, $"chapter -> {chapter}"), null);
    }

    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // VLC can raise TimeChanged ~190,000 times a second (seen when a Blu-ray menu loop ends and
        // stops). Queue a tick only when none is waiting, so the UI queue never holds more than one.
        if (Interlocked.Exchange(ref _timeTickPending, 1) == 1) return;
        _ui.Post(_ =>
        {
            Interlocked.Exchange(ref _timeTickPending, 0);
            _picks?.OnTimeTick();
        }, null);
    }

    private void SendMessage(MessageLevel level, string text)
    {
        Message?.Invoke(level, text);
    }

    // Core.Initialize() alone only looks beside the exe (AppContext.BaseDirectory). A single-file
    // publish unpacks VLC to a temp folder instead, so after the exe's folder also look through the
    // native DLL search directories .NET reports. Core.Initialize(dir) wants the folder that holds
    // libvlc.dll itself.
    private static string FindLibVlcFolder()
    {
        var searched = new[] { AppContext.BaseDirectory }
            .Concat(((string?)AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        foreach (var dir in searched)
        {
            var folder = Path.Combine(dir, "libvlc", "win-x64");
            if (File.Exists(Path.Combine(folder, "libvlc.dll")))
                return folder;
        }
        throw new InvalidOperationException(
            $"libvlc\\win-x64\\libvlc.dll not found in the exe folder or any native DLL search directory: {string.Join("; ", searched)}");
    }
}
