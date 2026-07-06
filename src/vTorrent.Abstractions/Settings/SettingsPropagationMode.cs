namespace vTorrent.Abstractions.Settings;

/// <summary>
/// How to propagate a global setting change to existing torrents.
/// </summary>
public enum SettingsPropagationMode
{
    /// <summary>Don't propagate — existing torrents keep their values.</summary>
    None,

    /// <summary>Reset ALL torrents to use global (set per-torrent override to sentinel).</summary>
    OverrideAll,

    /// <summary>Only reset torrents whose per-torrent value matches the old global default.</summary>
    OnlyMatchingOldDefault
}
