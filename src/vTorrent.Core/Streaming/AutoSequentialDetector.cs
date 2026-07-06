namespace vTorrent.Core.Streaming;

/// <summary>
/// Detects when auto-sequential mode should activate based on swarm composition.
/// libtorrent equivalent: torrent::update_auto_sequential() (torrent.cpp:3685-3708).
/// Condition: seeds >= 10 AND seeds >= 10 * downloaders.
/// </summary>
internal static class AutoSequentialDetector
{
    public static bool ShouldEnable(int seeds, int downloaders)
        => seeds > 9 && seeds >= downloaders * 10;
}
