using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using vTorrent.Abstractions.TorrentSigning;
using vTorrent.Bencode.Objects;
using vTorrent.Core.TorrentSigning;
using Xunit;

namespace vTorrent.Core.Tests.TorrentSigning;

public class TorrentSignatureVerifierTests
{
    private readonly TorrentSignatureVerifier _verifier = new();

    private class InMemoryTrustStore : ITrustStore
    {
        private readonly Dictionary<string, TrustedCertificate> _certs = new();

        public bool IsTrusted(string fingerprint) => _certs.ContainsKey(fingerprint);
        public TrustedCertificate? GetByFingerprint(string fingerprint) =>
            _certs.TryGetValue(fingerprint, out var c) ? c : null;
        public TrustedCertificate? GetBySignerName(string signerName) => null;
        public List<TrustedCertificate> GetAll() => new(_certs.Values);
        public void Add(TrustedCertificate cert) => _certs[cert.Fingerprint] = cert;
        public void Remove(string fingerprint) => _certs.Remove(fingerprint);
    }

    private (RSA key, X509Certificate2 cert) CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = cert.Export(X509ContentType.Pfx);
        var reimported = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
        return (reimported.GetRSAPrivateKey()!, reimported);
    }

    private (BDictionary torrentDict, byte[] rawBytes) CreateSignedTorrent(
        RSA privateKey, X509Certificate2 cert, string signerName = "com.test")
    {
        var infoDict = new BDictionary();
        infoDict.AddNumber("length", 1000);
        infoDict.AddString("name", "test.txt");
        infoDict.AddNumber("piece length", 16384);
        infoDict.AddBytes("pieces", new byte[20]);

        var infoBencoded = infoDict.EncodeAsBytes();

        var signature = privateKey.SignData(infoBencoded, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var signerEntry = new BDictionary();
        signerEntry.AddBytes("certificate", cert.RawData);
        signerEntry.AddBytes("signature", signature);

        var signaturesDict = new BDictionary();
        signaturesDict.Add(signerName, signerEntry);

        var torrentDict = new BDictionary();
        torrentDict.Add("info", infoDict);
        torrentDict.Add("signatures", signaturesDict);

        using var ms = new MemoryStream();
        torrentDict.EncodeTo(ms);
        var rawBytes = ms.ToArray();

        return (torrentDict, rawBytes);
    }

    [Fact]
    public void Verify_NoSignatures_ReturnsEmptyList()
    {
        var dict = new BDictionary();
        var infoDict = new BDictionary();
        infoDict.AddString("name", "test");
        dict.Add("info", infoDict);
        using var ms = new MemoryStream();
        dict.EncodeTo(ms);
        var raw = ms.ToArray();
        var trust = new InMemoryTrustStore();

        var results = _verifier.Verify(dict, raw, trust);
        results.Should().BeEmpty();
    }

    [Fact]
    public void Verify_ValidSignature_Trusted_ReturnsValid()
    {
        var (key, cert) = CreateSelfSignedCert();
        var (torrentDict, rawBytes) = CreateSignedTorrent(key, cert);

        var trust = new InMemoryTrustStore();
        var fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData));
        trust.Add(new TrustedCertificate
        {
            Fingerprint = fingerprint,
            Label = "Test",
            CertificateData = cert.RawData,
            AddedDate = DateTime.UtcNow
        });

        var results = _verifier.Verify(torrentDict, rawBytes, trust);
        results.Should().ContainSingle();
        results[0].Status.Should().Be(SignatureStatus.Valid);
        results[0].SignerName.Should().Be("com.test");
    }

    [Fact]
    public void Verify_ValidSignature_Untrusted_ReturnsValidUntrusted()
    {
        var (key, cert) = CreateSelfSignedCert();
        var (torrentDict, rawBytes) = CreateSignedTorrent(key, cert);
        var trust = new InMemoryTrustStore();

        var results = _verifier.Verify(torrentDict, rawBytes, trust);
        results.Should().ContainSingle();
        results[0].Status.Should().Be(SignatureStatus.ValidUntrusted);
    }

    [Fact]
    public void Verify_TamperedInfo_ReturnsInvalid()
    {
        var (key, cert) = CreateSelfSignedCert();
        var (torrentDict, rawBytes) = CreateSignedTorrent(key, cert);

        var tampered = (byte[])rawBytes.Clone();
        // Find "test.txt" in the raw bytes and change it
        for (int i = 0; i < tampered.Length - 3; i++)
        {
            if (tampered[i] == (byte)'t' && tampered[i + 1] == (byte)'e'
                && tampered[i + 2] == (byte)'s' && tampered[i + 3] == (byte)'t')
            {
                tampered[i] = (byte)'T'; // Tamper
                break;
            }
        }

        var results = _verifier.Verify(torrentDict, tampered, new InMemoryTrustStore());
        results.Should().ContainSingle();
        results[0].Status.Should().Be(SignatureStatus.Invalid);
    }

    [Fact]
    public void Verify_ExpiredCert_ReturnsExpired()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Expired", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var expiredCert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddYears(-2),
            DateTimeOffset.UtcNow.AddYears(-1));

        var pfx = expiredCert.Export(X509ContentType.Pfx);
        var reimported = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
        var privateKey = reimported.GetRSAPrivateKey()!;

        var (torrentDict, rawBytes) = CreateSignedTorrent(privateKey, reimported);
        var results = _verifier.Verify(torrentDict, rawBytes, new InMemoryTrustStore());
        results.Should().ContainSingle();
        results[0].Status.Should().Be(SignatureStatus.Expired);
    }
}
