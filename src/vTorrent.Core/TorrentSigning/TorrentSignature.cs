using vTorrent.Bencode.Objects;

namespace vTorrent.Core.TorrentSigning;

/// <summary>
/// BEP 35: Represents a single signer's signature on a torrent.
/// </summary>
public class TorrentSignature
{
    /// <summary>
    /// Signer identifier in reverse-DNS notation (e.g., "com.example").
    /// </summary>
    public string SignerName { get; set; } = string.Empty;

    /// <summary>
    /// X.509 DER-encoded certificate (nullable — BEP 35 says optional).
    /// </summary>
    public byte[]? Certificate { get; set; }

    /// <summary>
    /// RSA signature bytes (null when signature field is missing from torrent).
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// Optional "info" sub-dict from the signature entry.
    /// When present, its bencoded bytes are concatenated with the main info dict for verification.
    /// </summary>
    public BDictionary? SignatureInfo { get; set; }

    /// <summary>
    /// Verification result.
    /// </summary>
    public SignatureStatus Status { get; set; }
}
