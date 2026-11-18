// ============================================================
// BluRayPicks.cs
// ------------------------------------------------------------
// Blu-ray: picks and skips are decided per playlist, from libbluray's
// log (BdLogReader). After Enter, the first content playlist (time
// search allowed) is the pick; once its start position is settled it
// is matched and skipped to 1 s before its end. Start-up asks for the
// top menu until the disc takes it.
// ============================================================

using System.IO;
using LibVLCSharp.Shared;

namespace TranscodeTools;

internal sealed class BluRayPicks : IDiscPicks
{
    private readonly MediaPlayer _mediaPlayer;
    private readonly Action<MessageLevel, string> _send;
    private readonly Action _topMenu;
    private readonly BdLogReader _bdLog;
    private readonly Dictionary<string, MplsPlaylist> _playlistInfo = new(); // every .mpls, decoded at Open
    private readonly List<PlaylistEvent> _playlists = new();
    private int _currentTitle;
    private string? _currentPlaylist;
    private bool _enterPressed;     // Enter pressed since the disc was last back on the menu
    private string? _pickPlaylist;  // the first content playlist since Enter
    private int _pickTitle;
    private int _playItem;          // libbluray's current play item (PSR7) in the current playlist
    private bool _pickStartPending; // the pick is waiting for its start position (first seek or play mark)
    private PickStart? _pickStart;
    private bool _startMenuPending = true; // start-up: still asking for the top menu
    private bool _startMenuSent;           // a start-up menu call has been sent
    private bool _startMenuMasked;         // the disc blocked the last menu call

    public event Action<PickResult>? BackOnMenu;

    public BluRayPicks(string discPath, MediaPlayer mediaPlayer, Action<MessageLevel, string> send,
        BdLogReader bdLog, Action topMenu)
    {
        _mediaPlayer = mediaPlayer;
        _send = send;
        _topMenu = topMenu;
        _bdLog = bdLog;
        DecodePlaylists(discPath);
        _bdLog.PlaylistSelected += OnPlaylist;
        _bdLog.MenuCallMasked += OnMenuCallMasked;
        _bdLog.PlayItemChanged += OnPlayItem;
        _bdLog.PlayPosition += OnPlayPosition;
    }

    // The log reader outlives this disc, so the subscriptions are removed here
    public void Dispose()
    {
        _bdLog.PlaylistSelected -= OnPlaylist;
        _bdLog.MenuCallMasked -= OnMenuCallMasked;
        _bdLog.PlayItemChanged -= OnPlayItem;
        _bdLog.PlayPosition -= OnPlayPosition;
    }

    public void OnEnter()
    {
        if (_startMenuPending)
        {
            // A menu call after Enter could land on the title just picked, so the start-up asking ends here
            _startMenuPending = false;
            _send(MessageLevel.Info, "start-up: Enter pressed, no more asking");
        }
        if (!_enterPressed)
        {
            // Only titles that start after Enter are counted, so the playlist list starts here too
            _enterPressed = true;
            _pickPlaylist = null;
            _playlists.Clear();
        }
    }

    // A title change only matters as the return to the menu, which is the first title 0 after the
    // pick was skipped. Title 0 before a pick (Enter's drop, interstitials, a start-up chain) leaves
    // Enter counting. Title 0 while the pick still waits for its start position is a drop, not the
    // return: LOGICAL_VOLUME_ID's feature drops to title 0 ~200 ms after its playlist starts, before
    // the player reads the line that settles the start.
    // The pick's title is the one current when its playlist starts; if that is 0 (VLC can report
    // the title after the playlist starts, as on BLUES_BROTHERS_DOM 00300), the first non-zero
    // title while the pick is current is taken. A drop to 0 never replaces it (LOGICAL_VOLUME_ID).
    public void OnTitle(int title)
    {
        if (title == _currentTitle) return;
        _currentTitle = title;
        _send(MessageLevel.Info, $"title {title} started");
        if (title != 0 && _pickPlaylist is not null && _pickTitle == 0)
        {
            _pickTitle = title;
            _send(MessageLevel.Info, $"playlist {_pickPlaylist}: title {title} taken as the pick's title (it was 0 when the playlist started)");
        }
        if (title != 0 || _pickPlaylist is null) return;
        if (_pickStartPending)
        {
            _send(MessageLevel.Info, $"playlist {_pickPlaylist}: title 0 before its start position, not counted as the menu");
            return;
        }

        var pick = _pickPlaylist;
        var playlists = _playlists.ToArray();
        var start = _pickStart;
        _enterPressed = false;
        _pickPlaylist = null;
        _pickStart = null;
        _playlists.Clear();
        _send(MessageLevel.Info, $"back on the menu, picked: title {_pickTitle}, playlist {pick}");
        var played = new[] { new PlayedTitle(_pickTitle, _playlistInfo[pick].Length, "") };
        BackOnMenu?.Invoke(new PickResult(DiscType.BluRay, played, pick, start, playlists));
    }

    // Blu-ray skips per playlist (OnPlayPosition / SkipAfterEnter), not on time
    public void OnTimeTick()
    {
    }

    // Jump the playing playlist to 1 s before its end, length taken from its .mpls. No key is mapped
    // to it yet.
    public void SkipPlaylist()
    {
        if (_currentPlaylist is null)
        {
            _send(MessageLevel.Info, "skip: no playlist playing");
            return;
        }
        if (!_playlistInfo.TryGetValue(_currentPlaylist, out var info))
        {
            _send(MessageLevel.Info, $"skip: playlist {_currentPlaylist} not decoded");
            return;
        }
        var length = info.Length;
        var target = (long)length.TotalMilliseconds - 1000;
        _send(MessageLevel.Info, $"skip: playlist {_currentPlaylist} length {length:hh\\:mm\\:ss}, VLC time {_mediaPlayer.Time} ms -> {target} ms");
        _mediaPlayer.Time = target;
    }

    private void DecodePlaylists(string discPath)
    {
        var rejected = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(discPath, "BDMV", "PLAYLIST"), "*.mpls"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                _playlistInfo[name] = MplsReader.Read(file);
            }
            catch (InvalidDataException ex)
            {
                rejected++;
                _send(MessageLevel.Info, $"playlist {name} rejected: {ex.Message}");
            }
        }
        var content = _playlistInfo.Values.Count(p => !p.BlocksTimeSearch);
        _send(MessageLevel.Info, $"decoded {_playlistInfo.Count} playlists, {content} content (time search allowed), {rejected} rejected");
    }

    private void OnPlayItem(int item) => _playItem = item;

    private void OnPlaylist(string playlist)
    {
        if (playlist == _currentPlaylist) return;
        _currentPlaylist = playlist;
        _playItem = 0; // libbluray resets PSR7 when it opens a playlist
        _playlists.Add(new PlaylistEvent(DateTime.Now, _currentTitle, playlist));
        _send(MessageLevel.Info, $"playlist {playlist} (title {_currentTitle})");
        if (_enterPressed)
            SkipAfterEnter(playlist);
        SkipStartUpToMenu();
    }

    // After Enter: the first content playlist (time search allowed) is the title the pick calls. Only
    // content is skipped; a playlist that blocks time search (bumpers, interstitials, menus) is left to
    // play, since the disc marks seeking there as not allowed. At its default (RELAXED) level libbluray
    // doesn't block the seek itself (11-06), but on BLUES_BROTHERS_DOM 00053/00055 restarted from the
    // beginning after one.
    // A playlist rejected at Open (DecodePlaylists) is left to play: it is never the pick and never
    // skipped, so everything past this point only sees decoded playlists.
    private void SkipAfterEnter(string playlist)
    {
        if (!_playlistInfo.TryGetValue(playlist, out var info))
        {
            _send(MessageLevel.Info, $"playlist {playlist} left to play, not decoded");
            return;
        }
        if (info.BlocksTimeSearch)
        {
            _send(MessageLevel.Info, $"playlist {playlist} left to play (non-content, time search blocked)");
            return;
        }
        if (_pickPlaylist is null)
        {
            _pickPlaylist = playlist;
            _pickTitle = _currentTitle;
            _pickStartPending = true;
            _pickStart = null;
            _send(MessageLevel.Info, $"playlist {playlist} is the pick (content, title {_currentTitle}), waiting for its start position");
            return; // skipped once the start position is settled (OnPlayPosition)
        }
        SkipToEnd(playlist);
    }

    private void SkipToEnd(string playlist)
    {
        var info = _playlistInfo[playlist];
        if (info.Length < TimeSpan.FromSeconds(1))
        {
            _send(MessageLevel.Info, $"playlist {playlist} left to play (under 1 s)");
            return;
        }
        var target = (long)info.Length.TotalMilliseconds - 1000;
        _send(MessageLevel.Info, $"skipping playlist {playlist} (content) to {TimeSpan.FromMilliseconds(target):hh\\:mm\\:ss}");
        _mediaPlayer.Time = target;
    }

    // The pick's start position is settled: record the clip it started on, match it, then skip.
    // Partway in (play item > 0): the single-clip content playlist(s) of that clip. At the beginning:
    // the pick's own playlist, plus any single-clip content playlist of its first clip when the pick
    // has more than one clip (ambiguous).
    private void OnPlayPosition()
    {
        if (!_pickStartPending || _pickPlaylist is null) return;
        _pickStartPending = false;

        var pick = _pickPlaylist;
        var clips = _playlistInfo[pick].Clips;
        if (_playItem >= clips.Count)
        {
            _send(MessageLevel.Info, $"playlist {pick}: play item {_playItem} is past its {clips.Count} clips, no match");
            _pickStart = new PickStart(_playItem, "?", Array.Empty<PickCandidate>());
            SkipToEnd(pick);
            return;
        }

        var clip = clips[_playItem];
        var own = _playlistInfo
            .Where(p => p.Key != pick && !p.Value.BlocksTimeSearch && p.Value.Clips.Count == 1 && p.Value.Clips[0] == clip)
            .Select(p => p.Key)
            .OrderBy(p => p, StringComparer.Ordinal);
        var matches = _playItem > 0 ? own.ToList()
            : clips.Count > 1 ? own.Prepend(pick).ToList()
            : new List<string> { pick };
        _pickStart = new PickStart(_playItem, clip,
            matches.Select(p => new PickCandidate(p, _playlistInfo[p].Length)).ToList());
        _send(MessageLevel.Info,
            $"playlist {pick} started on clip {clip} (play item {_playItem}), match: {(matches.Count == 0 ? "none" : string.Join(" or ", matches))}");
        SkipToEnd(pick);
    }

    // Start-up: ask for the top menu as each playlist starts until the disc takes it. A disc can block
    // the call (Drunken Master's language screen does); libbluray logs that as "UO menu call masked"
    // but logs nothing when a call is accepted, so a playlist starting after an unblocked call is what
    // ends the asking.
    private void SkipStartUpToMenu()
    {
        if (!_startMenuPending) return;
        if (_startMenuSent && !_startMenuMasked)
        {
            _startMenuPending = false;
            _send(MessageLevel.Info, "start-up: menu call accepted, no more asking");
            return;
        }
        _startMenuSent = true;
        _startMenuMasked = false;
        _send(MessageLevel.Info, "start-up: asking for the top menu");
        _topMenu();
    }

    private void OnMenuCallMasked()
    {
        _startMenuMasked = true;
        _send(MessageLevel.Info, "menu call blocked by the disc");
    }
}
