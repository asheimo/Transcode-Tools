// ============================================================
// BdLogReader.cs
// ------------------------------------------------------------
// Follows libbluray's debug log and raises the events Blu-ray picks
// are decided from. libbluray reads BD_DEBUG_MASK / BD_DEBUG_FILE
// when it loads, so this must be created before LibVLC is.
//
// At a pick the order is: playlist selected -> PSR7 write (none when
// the start is play item 0) -> the disc's first "Seek to" (a start
// from a chapter mark, SPYxFAMILY) or the first "PlayMark N reached"
// (a plain start, Picard). Either one is where the pick's start
// position is settled.
//
// Failures:
//   - A file error while reading the log is retried on the next tick.
//   - Anything else (a bug in an event handler) stops the reader and
//     raises Failed, so the failure is shown, never silent.
//   - Failed is also raised when the log has not appeared
//     LogAppearTimeout after Start: libbluray only writes it when
//     _putenv reaches its C runtime (msvcrt.dll for VLC 3.0.24), and
//     without it there are no Blu-ray picks.
//
// Start and Stop follow one disc at a time; the reader itself lives
// as long as the player.
// ============================================================

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TranscodeTools;

internal sealed class BdLogReader : IDisposable
{
    private const string DebugMask = "0x2940"; // BLURAY | NAV | CRIT | BDJ
    private static readonly TimeSpan LogAppearTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex LogEvent = new(
        @"Title (?<playlist>\d{5})\.mpls selected"
        + @"|(?<masked>bd_menu_call\(\): UO menu call masked)"
        + @"|PSR7\s+\(PLAYITEM\) 0x[0-9a-fA-F]+ -> 0x(?<item>[0-9a-fA-F]+)"
        + @"|(?<position>Seek to \d+|PlayMark \d+ reached)",
        RegexOptions.Compiled);

    private readonly string _path;
    private readonly Action<MessageLevel, string> _send;
    private CancellationTokenSource? _stop; // set while following
    private long _position;

    public event Action<string>? PlaylistSelected;
    public event Action? MenuCallMasked;
    public event Action<int>? PlayItemChanged;
    public event Action? PlayPosition;
    public event Action<Exception>? Failed;

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int _putenv(string envString);

    public BdLogReader(string path, Action<MessageLevel, string> send)
    {
        _path = path;
        _send = send;
        File.Delete(_path);
        SetCrtEnvironment("BD_DEBUG_MASK", DebugMask);
        SetCrtEnvironment("BD_DEBUG_FILE", _path);
    }

    // Call on the UI thread: the reads resume there, so the events are raised on it too. Reading
    // carries on from where the last Stop left it.
    public void Start()
    {
        Stop();
        _stop = new CancellationTokenSource();
        _ = FollowAsync(_stop.Token);
        _ = CheckLogAppearsAsync(_stop.Token);
    }

    public void Stop()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private static void SetCrtEnvironment(string name, string value)
    {
        if (_putenv($"{name}={value}") != 0)
            throw new InvalidOperationException($"_putenv failed for {name}");
    }

    private async Task CheckLogAppearsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(LogAppearTimeout, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var file = new FileInfo(_path);
        if (file.Exists && file.Length > 0) return;

        var message = $"libbluray's debug log never appeared at {_path} ({LogAppearTimeout.TotalSeconds:0} s after the disc opened)";
        _send(MessageLevel.Info, message);
        Failed?.Invoke(new InvalidOperationException(message));
    }

    private async Task FollowAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                Read();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _send(MessageLevel.Info, $"bd log reader stopped: {ex.Message}");
            Failed?.Invoke(ex);
        }
    }

    // The events are raised outside the file-error catch, so only reading the file is retried
    private void Read()
    {
        string? text;
        try
        {
            text = ReadNewLines();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _send(MessageLevel.Debug, $"bd log not read, trying again: {ex.Message}");
            return;
        }
        if (text is null) return;

        foreach (Match match in LogEvent.Matches(text))
        {
            if (match.Groups["playlist"].Success)
                PlaylistSelected?.Invoke(match.Groups["playlist"].Value);
            else if (match.Groups["masked"].Success)
                MenuCallMasked?.Invoke();
            else if (match.Groups["item"].Success)
                PlayItemChanged?.Invoke(Convert.ToInt32(match.Groups["item"].Value, 16));
            else
                PlayPosition?.Invoke();
        }
    }

    // The complete lines added since the last read, or null when there are none
    private string? ReadNewLines()
    {
        if (!File.Exists(_path)) return null;

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var available = stream.Length - _position;
        if (available <= 0) return null;

        var buffer = new byte[available];
        stream.Position = _position;
        stream.ReadExactly(buffer);

        var end = Array.LastIndexOf(buffer, (byte)'\n');
        if (end < 0) return null;
        _position += end + 1;

        return Encoding.UTF8.GetString(buffer, 0, end + 1);
    }
}
