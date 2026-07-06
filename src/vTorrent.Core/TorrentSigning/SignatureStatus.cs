namespace vTorrent.Core.TorrentSigning;

/// <summary>
/// BEP 35: Result of signature verification.
/// </summary>
public enum SignatureStatus
{
    /// <summary>Signature verifies and signer is in trust store.</summary>
    Valid,

    /// <summary>Signature verifies but signer is not in trust store.</summary>
    ValidUntrusted,

    /// <summary>Signature fails verification.</summary>
    Invalid,

    /// <summary>No signature present in torrent.</summary>
    NoCertificate,

    /// <summary>Certificate has expired (signature may be valid).</summary>
    Expired
}
