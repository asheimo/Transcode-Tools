// ============================================================
// IDiscPicks.cs
// ------------------------------------------------------------
// How one disc type decides which title the user picked and sends it
// back to the menu. DiscPlayer creates one at Open, routes VLC's
// events to it and forwards BackOnMenu. Each implementation keeps its
// own state, so a change to one disc type can't reach the other.
// Disposed when the disc closes, so it stops listening to anything
// it subscribed to.
// ============================================================

namespace TranscodeTools;

internal interface IDiscPicks : IDisposable
{
    event Action<PickResult>? BackOnMenu;

    void OnEnter();
    void OnTitle(int title);
    void OnTimeTick();
    void SkipPlaylist();
}
