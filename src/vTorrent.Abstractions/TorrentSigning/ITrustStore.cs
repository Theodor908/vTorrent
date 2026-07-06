using System.Collections.Generic;

namespace vTorrent.Abstractions.TorrentSigning;

/// <summary>
/// Interface for managing trusted certificates.
/// </summary>
public interface ITrustStore
{
    /// <summary>Checks if a certificate with this fingerprint is trusted.</summary>
    bool IsTrusted(string fingerprint);

    /// <summary>Gets a trusted certificate by fingerprint, or null.</summary>
    TrustedCertificate? GetByFingerprint(string fingerprint);

    /// <summary>Gets a trusted certificate by signer name, or null.</summary>
    TrustedCertificate? GetBySignerName(string signerName);

    /// <summary>Gets all trusted certificates.</summary>
    List<TrustedCertificate> GetAll();

    /// <summary>Adds a certificate to the trust store.</summary>
    void Add(TrustedCertificate cert);

    /// <summary>Removes a certificate by fingerprint.</summary>
    void Remove(string fingerprint);
}
