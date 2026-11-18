// ============================================================
// DvdPicks.cs
// ------------------------------------------------------------
// DVD: a title that starts after Enter is the pick. One second in,
// its length is read from VLC and it is skipped to 1 s before its
// end, so the disc's own end-of-title commands return to the menu.
// While VLC has no length yet (0 or less), each later tick asks
// again. Titles under MinPlayedLength still skip but are left out of
// what was played.
// ============================================================

using LibVLCSharp.Shared;

namespace TranscodeTools;

internal sealed class DvdPicks : IDiscPicks
{
    private const long MinPlayedLength = 20_000; // milliseconds

    private readonly MediaPlayer _mediaPlayer;
    private readonly Action<MessageLevel, string> _send;
    private readonly List<PlayedTitle> _played = new();
    private int _currentTitle;
    private DateTime _titleStarted;
    private bool _skipDone;
    private bool _lengthWaitLogged; // "no length yet" logged once per title
    private bool _enterPressed;  // Enter pressed since the disc was last back on the menu
    private bool _titleChosen;   // the current title started while _enterPressed was set

    public event Action<PickResult>? BackOnMenu;

    public DvdPicks(MediaPlayer mediaPlayer, Action<MessageLevel, string> send)
    {
        _mediaPlayer = mediaPlayer;
        _send = send;
    }

    public void OnEnter()
    {
        _enterPressed = true;
    }

    public void OnTitle(int title)
    {
        if (title == _currentTitle) return;
        _currentTitle = title;
        _titleStarted = DateTime.Now;
        _skipDone = false;
        _lengthWaitLogged = false;

        if (title == 0)
        {
            _enterPressed = false;
            _titleChosen = false;
            if (_played.Count == 0) return;

            var played = _played.ToArray();
            _played.Clear();
            _send(MessageLevel.Info, $"back on the menu, played: {string.Join(",", played.Select(t => t.Number))}, playlist: none");
            BackOnMenu?.Invoke(new PickResult(DiscType.Dvd, played, null, null, Array.Empty<PlaylistEvent>()));
            return;
        }

        _titleChosen = _enterPressed;
        _send(MessageLevel.Info, _titleChosen
            ? $"title {title} started"
            : $"title {title} started without Enter, left to play");
    }

    public void OnTimeTick()
    {
        if (_currentTitle == 0 || _skipDone || !_titleChosen) return;
        if (DateTime.Now - _titleStarted < TimeSpan.FromSeconds(1)) return;

        var length = _mediaPlayer.Length;
        if (length <= 0)
        {
            if (!_lengthWaitLogged)
            {
                _lengthWaitLogged = true;
                _send(MessageLevel.Info, $"title {_currentTitle}: VLC has no length yet, asking again");
            }
            return;
        }

        _skipDone = true;
        _send(MessageLevel.Info, $"title {_currentTitle} length {TimeSpan.FromMilliseconds(length):hh\\:mm\\:ss}");

        if (length >= MinPlayedLength)
            _played.Add(new PlayedTitle(_currentTitle, TimeSpan.FromMilliseconds(length), ""));

        if (length < 1000)
        {
            _send(MessageLevel.Info, $"title {_currentTitle} left to play");
            return;
        }

        var target = length - 1000;
        _send(MessageLevel.Info, $"title {_currentTitle} skipping to {TimeSpan.FromMilliseconds(target):hh\\:mm\\:ss}");
        _mediaPlayer.Time = target;
    }

    // DVD has no playlists
    public void SkipPlaylist()
    {
        _send(MessageLevel.Info, "skip: no playlist playing");
    }

    // Nothing subscribed to
    public void Dispose()
    {
    }
}
