namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Resolves per-torrent setting overrides against global defaults.
/// Nullable enums/bools: null = use global. Ints: -1 = use global.
/// </summary>
public static class SettingsResolver
{
    public static T Resolve<T>(T? perTorrent, T global) where T : struct
        => perTorrent ?? global;

    public static int Resolve(int perTorrent, int global)
        => perTorrent == -1 ? global : perTorrent;
}
