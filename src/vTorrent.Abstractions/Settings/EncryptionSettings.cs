using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Encryption settings matching libtorrent's pe_settings.
/// Controls MSE/PE (Message Stream Encryption / Protocol Encryption).
/// </summary>
public class EncryptionSettings
{
    /// <summary>Encryption policy for outgoing connections.</summary>
    public EncryptionPolicy OutPolicy { get; set; } = EncryptionPolicy.Enabled;

    /// <summary>Encryption policy for incoming connections.</summary>
    public EncryptionPolicy InPolicy { get; set; } = EncryptionPolicy.Enabled;

    /// <summary>Allowed encryption level (plaintext obfuscation, RC4 stream, or both).</summary>
    public EncryptionLevel AllowedLevel { get; set; } = EncryptionLevel.Both;

}
