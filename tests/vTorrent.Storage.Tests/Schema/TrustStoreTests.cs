using System;
using System.Security.Cryptography;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using vTorrent.Abstractions.TorrentSigning;
using Xunit;

namespace vTorrent.Storage.Tests.Schema;

public class TrustStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTrustStore _store;

    public TrustStoreTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _connection.Execute(@"
            CREATE TABLE trusted_certificates (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                fingerprint TEXT NOT NULL UNIQUE,
                label TEXT NOT NULL,
                certificate_data BLOB NOT NULL,
                signer_name TEXT,
                added_date TEXT NOT NULL
            )");
        _store = new SqliteTrustStore(_connection);
    }

    public void Dispose() => _connection.Dispose();

    private TrustedCertificate MakeCert(string label = "test")
    {
        var certData = new byte[100];
        RandomNumberGenerator.Fill(certData);
        var fingerprint = Convert.ToHexString(SHA256.HashData(certData));
        return new TrustedCertificate
        {
            Fingerprint = fingerprint,
            Label = label,
            CertificateData = certData,
            AddedDate = DateTime.UtcNow
        };
    }

    [Fact]
    public void Add_ThenIsTrusted_ReturnsTrue()
    {
        var cert = MakeCert();
        _store.Add(cert);
        _store.IsTrusted(cert.Fingerprint).Should().BeTrue();
    }

    [Fact]
    public void IsTrusted_UnknownFingerprint_ReturnsFalse()
    {
        _store.IsTrusted("AAAA").Should().BeFalse();
    }

    [Fact]
    public void GetAll_ReturnsAllCerts()
    {
        _store.Add(MakeCert("a"));
        _store.Add(MakeCert("b"));
        _store.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void Remove_ThenIsTrusted_ReturnsFalse()
    {
        var cert = MakeCert();
        _store.Add(cert);
        _store.Remove(cert.Fingerprint);
        _store.IsTrusted(cert.Fingerprint).Should().BeFalse();
    }

    [Fact]
    public void GetByFingerprint_Found_ReturnsCert()
    {
        var cert = MakeCert("found");
        _store.Add(cert);
        var result = _store.GetByFingerprint(cert.Fingerprint);
        result.Should().NotBeNull();
        result!.Label.Should().Be("found");
        result.CertificateData.Should().BeEquivalentTo(cert.CertificateData);
    }

    [Fact]
    public void GetByFingerprint_NotFound_ReturnsNull()
    {
        _store.GetByFingerprint("ZZZZ").Should().BeNull();
    }

    [Fact]
    public void Add_DuplicateFingerprint_Replaces()
    {
        var cert = MakeCert("original");
        _store.Add(cert);

        cert.Label = "updated";
        _store.Add(cert);

        _store.GetAll().Should().HaveCount(1);
        _store.GetByFingerprint(cert.Fingerprint)!.Label.Should().Be("updated");
    }
}
