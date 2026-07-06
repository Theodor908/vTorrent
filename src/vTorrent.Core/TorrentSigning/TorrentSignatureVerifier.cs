using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using vTorrent.Bencode.Objects;

namespace vTorrent.Core.TorrentSigning;

/// <summary>
/// BEP 35: Verifies torrent signatures.
/// Requires both parsed dict and raw bytes to extract verbatim info dict bytes.
/// </summary>
public class TorrentSignatureVerifier
{
    private readonly ILogger? _logger;

    public TorrentSignatureVerifier(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verifies all signatures in a torrent file.
    /// </summary>
    public List<TorrentSignature> Verify(
        BDictionary torrentDict, ReadOnlySpan<byte> rawTorrentBytes, ITrustStore trustStore)
    {
        var results = new List<TorrentSignature>();

        if (!torrentDict.TryGetValue("signatures", out var sigObj) || sigObj is not BDictionary sigDict)
        {
            return results; // No signatures — not an error, just unsigned
        }

        // Extract verbatim info dict bytes from raw torrent
        var infoDictBytes = ExtractInfoDictBytes(rawTorrentBytes);
        if (infoDictBytes.Length == 0)
        {
            _logger?.LogWarning("[BEP35] Could not extract info dict bytes from torrent");
            return results;
        }

        foreach (var kvp in sigDict)
        {
            var signerName = kvp.Key.ToString();
            if (kvp.Value is not BDictionary signerEntry)
                continue;

            var result = VerifySigner(signerName, signerEntry, infoDictBytes, trustStore);
            results.Add(result);
        }

        return results;
    }

    private TorrentSignature VerifySigner(
        string signerName, BDictionary signerEntry,
        ReadOnlySpan<byte> infoDictBytes, ITrustStore trustStore)
    {
        var result = new TorrentSignature { SignerName = signerName };

        // Extract signature bytes
        if (!signerEntry.TryGetValue("signature", out var sigValObj) || sigValObj is not BString sigValStr)
        {
            _logger?.LogWarning("[BEP35] Missing 'signature' field for signer {Signer}", signerName);
            result.Status = SignatureStatus.Invalid;
            return result;
        }
        result.Signature = sigValStr.Value.ToArray();

        // Extract optional certificate
        byte[]? certBytes = null;
        if (signerEntry.TryGetValue("certificate", out var certObj) && certObj is BString certStr)
        {
            certBytes = certStr.Value.ToArray();
            result.Certificate = certBytes;
        }

        // Extract optional signature info sub-dict
        if (signerEntry.TryGetValue("info", out var infoObj) && infoObj is BDictionary infoDictObj)
        {
            result.SignatureInfo = infoDictObj;
        }

        // Resolve certificate: from torrent or trust store
        X509Certificate2? cert = null;
        if (certBytes != null)
        {
            try { cert = new X509Certificate2(certBytes); }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[BEP35] Invalid certificate for signer {Signer}", signerName);
                result.Status = SignatureStatus.Invalid;
                return result;
            }
        }
        else
        {
            // Try to find certificate in trust store by signer name
            var trusted = trustStore.GetBySignerName(signerName);
            if (trusted?.CertificateData != null)
            {
                try { cert = new X509Certificate2(trusted.CertificateData); }
                catch { /* fall through to NoCertificate */ }
            }
        }

        if (cert == null)
        {
            result.Status = SignatureStatus.NoCertificate;
            return result;
        }

        // Build signing input: info dict bytes + optional signature info bytes
        byte[] signingInput;
        if (result.SignatureInfo != null)
        {
            var sigInfoBytes = result.SignatureInfo.EncodeAsBytes();
            signingInput = new byte[infoDictBytes.Length + sigInfoBytes.Length];
            infoDictBytes.CopyTo(signingInput);
            sigInfoBytes.CopyTo(signingInput.AsSpan(infoDictBytes.Length));
        }
        else
        {
            signingInput = infoDictBytes.ToArray();
        }

        // Verify RSA signature using the certificate's public key
        using var rsa = cert.GetRSAPublicKey();
        if (rsa == null)
        {
            _logger?.LogWarning("[BEP35] Certificate for {Signer} has no RSA public key", signerName);
            result.Status = SignatureStatus.Invalid;
            return result;
        }

        // Detect hash algorithm from certificate's signature algorithm
        var hashAlgorithm = GetHashAlgorithmFromCert(cert);

        bool isValid;
        try
        {
            isValid = rsa.VerifyData(signingInput, result.Signature, hashAlgorithm, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[BEP35] Signature verification failed for {Signer}", signerName);
            result.Status = SignatureStatus.Invalid;
            return result;
        }

        if (!isValid)
        {
            result.Status = SignatureStatus.Invalid;
            return result;
        }

        // Check certificate expiration
        if (cert.NotAfter.ToUniversalTime() < DateTime.UtcNow)
        {
            result.Status = SignatureStatus.Expired;
            return result;
        }

        // Check trust store
        var fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData));
        result.Status = trustStore.IsTrusted(fingerprint)
            ? SignatureStatus.Valid
            : SignatureStatus.ValidUntrusted;

        return result;
    }

    /// <summary>
    /// Extracts the raw bencoded info dictionary bytes from the raw torrent file.
    /// Scans for the "4:info" key and extracts the value that follows.
    /// </summary>
    private static ReadOnlySpan<byte> ExtractInfoDictBytes(ReadOnlySpan<byte> rawBytes)
    {
        // Search for "4:info" pattern — the info key followed by a bencoded value
        var needle = "4:info"u8;
        int pos = 0;
        while (pos < rawBytes.Length - needle.Length)
        {
            var idx = rawBytes.Slice(pos).IndexOf(needle);
            if (idx < 0) break;

            int valueStart = pos + idx + needle.Length;
            if (valueStart >= rawBytes.Length) break;

            // The value should be a bencoded type — find its extent
            int valueEnd = FindBencodeEnd(rawBytes, valueStart);
            if (valueEnd > valueStart)
                return rawBytes.Slice(valueStart, valueEnd - valueStart);

            pos = pos + idx + 1; // Try next occurrence
        }

        return ReadOnlySpan<byte>.Empty;
    }

    /// <summary>
    /// Finds the end position of a bencoded value starting at the given position.
    /// </summary>
    private static int FindBencodeEnd(ReadOnlySpan<byte> data, int start)
    {
        if (start >= data.Length) return start;

        byte first = data[start];

        // Dictionary or List: d...e or l...e
        if (first == (byte)'d' || first == (byte)'l')
        {
            int depth = 1;
            int pos = start + 1;
            while (pos < data.Length && depth > 0)
            {
                byte b = data[pos];
                if (b == (byte)'d' || b == (byte)'l')
                {
                    depth++;
                    pos++;
                }
                else if (b == (byte)'e')
                {
                    depth--;
                    pos++;
                }
                else if (b == (byte)'i')
                {
                    // Integer: i...e
                    int ePos = data.Slice(pos).IndexOf((byte)'e');
                    if (ePos < 0) return start; // malformed
                    pos += ePos + 1;
                }
                else if (b >= (byte)'0' && b <= (byte)'9')
                {
                    // String: length:data
                    int colonOffset = data.Slice(pos).IndexOf((byte)':');
                    if (colonOffset < 0) return start; // malformed
                    int strLen = 0;
                    for (int i = 0; i < colonOffset; i++)
                        strLen = strLen * 10 + (data[pos + i] - '0');
                    pos += colonOffset + 1 + strLen;
                }
                else
                {
                    return start; // malformed
                }
            }
            return pos;
        }

        // Integer: i...e
        if (first == (byte)'i')
        {
            int ePos = data.Slice(start).IndexOf((byte)'e');
            if (ePos < 0) return start;
            return start + ePos + 1;
        }

        // String: length:data
        if (first >= (byte)'0' && first <= (byte)'9')
        {
            int colonOffset = data.Slice(start).IndexOf((byte)':');
            if (colonOffset < 0) return start;
            int strLen = 0;
            for (int i = 0; i < colonOffset; i++)
                strLen = strLen * 10 + (data[start + i] - '0');
            return start + colonOffset + 1 + strLen;
        }

        return start;
    }

    private static HashAlgorithmName GetHashAlgorithmFromCert(X509Certificate2 cert)
    {
        var oid = cert.SignatureAlgorithm.Value;
        return oid switch
        {
            "1.2.840.113549.1.1.5" => HashAlgorithmName.SHA1,      // sha1WithRSAEncryption
            "1.2.840.113549.1.1.11" => HashAlgorithmName.SHA256,   // sha256WithRSAEncryption
            "1.2.840.113549.1.1.12" => HashAlgorithmName.SHA384,   // sha384WithRSAEncryption
            "1.2.840.113549.1.1.13" => HashAlgorithmName.SHA512,   // sha512WithRSAEncryption
            _ => HashAlgorithmName.SHA1 // Default for compatibility
        };
    }
}
