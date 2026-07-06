using System;

namespace vTorrent.Abstractions.TorrentSigning;

/// <summary>
/// A certificate the user has chosen to trust.
/// Persisted in SQLite.
/// </summary>
public class TrustedCertificate
{
    public int Id { get; set; }

    /// <summary>SHA-256 hex fingerprint of the DER-encoded certificate.</summary>
    public string Fingerprint { get; set; }

    /// <summary>User-assigned label for display.</summary>
    public string Label { get; set; }

    /// <summary>Raw X.509 DER-encoded certificate bytes.</summary>
    public byte[] CertificateData { get; set; }

    /// <summary>Reverse-DNS signer name associated with this certificate (e.g., "com.example").</summary>
    public string? SignerName { get; set; }

    /// <summary>When the user added this certificate.</summary>
    public DateTime AddedDate { get; set; }
}
