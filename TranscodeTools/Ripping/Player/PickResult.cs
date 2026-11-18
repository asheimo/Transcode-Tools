// ============================================================
// PickResult.cs
// ------------------------------------------------------------
// What one pick produced, raised once the disc is back on its menu.
// The key that joins it to a ripped file: on DVD the played title
// numbers, on Blu-ray the Playlist and Start's candidates. Playlists
// is every playlist that started since Enter (Blu-ray; empty on DVD).
// ============================================================

namespace TranscodeTools;

public sealed record PickResult(
    DiscType DiscType,
    IReadOnlyList<PlayedTitle> Played,
    string? Playlist,
    PickStart? Start,
    IReadOnlyList<PlaylistEvent> Playlists);

// One title the pick played. Length: VLC's on DVD, the playlist's .mpls on Blu-ray. Name: VLC's
// title name, added by DiscPlayer.
public sealed record PlayedTitle(int Number, TimeSpan Length, string Name);

public sealed record PlaylistEvent(DateTime At, int Title, string Playlist);
public sealed record PickCandidate(string Playlist, TimeSpan Length);
// Where the pick started inside its playlist, and the playlist(s) that match it. Two or more
// candidates = ambiguous: the pick started at the beginning of a multi-clip playlist whose first clip
// also has its own single-clip playlist (SPYxFAMILY episode 1 vs Play All), and the user chooses.
public sealed record PickStart(int PlayItem, string Clip, IReadOnlyList<PickCandidate> Candidates);
