namespace vTorrent.Abstractions.Settings.Enums;

/// <summary>
/// Allowed encryption level (matches libtorrent pe_settings::enc_level).
/// Maps to MSE crypto_provide/crypto_select bitflags.
/// </summary>
public enum EncryptionLevel
{
    /// <summary>DH handshake only, data unencrypted (crypto flag 0x01).</summary>
    Plaintext = 1,

    /// <summary>Full RC4 stream encryption (crypto flag 0x02).</summary>
    RC4 = 2,

    /// <summary>Offer/accept either method (crypto flags 0x03).</summary>
    Both = 3
}
