namespace vTorrent.Abstractions.Settings.Enums;

/// <summary>
/// Encryption policy for peer connections (matches libtorrent pe_settings::enc_policy).
/// </summary>
public enum EncryptionPolicy
{
    /// <summary>Only encrypted connections allowed (pe_forced).</summary>
    Forced = 0,

    /// <summary>Encrypted preferred, plaintext accepted (pe_enabled).</summary>
    Enabled = 1,

    /// <summary>Only plaintext connections allowed (pe_disabled).</summary>
    Disabled = 2
}
