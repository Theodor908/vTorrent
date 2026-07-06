using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using vTorrent.Abstractions.TorrentSigning;

namespace vTorrent.Storage;

/// <summary>
/// SQLite-backed trust store for BEP 35 certificates.
/// </summary>
public class SqliteTrustStore : ITrustStore
{
    private readonly SqliteConnection _connection;

    public SqliteTrustStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public bool IsTrusted(string fingerprint)
    {
        return _connection.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM trusted_certificates WHERE fingerprint = @fingerprint",
            new { fingerprint }) > 0;
    }

    public TrustedCertificate? GetByFingerprint(string fingerprint)
    {
        return _connection.QuerySingleOrDefault<TrustedCertificate>(
            "SELECT id, fingerprint, label, certificate_data, signer_name, added_date FROM trusted_certificates WHERE fingerprint = @fingerprint",
            new { fingerprint });
    }

    public TrustedCertificate? GetBySignerName(string signerName)
    {
        return _connection.QuerySingleOrDefault<TrustedCertificate>(
            "SELECT id, fingerprint, label, certificate_data, signer_name, added_date FROM trusted_certificates WHERE signer_name = @signerName",
            new { signerName });
    }

    public List<TrustedCertificate> GetAll()
    {
        return _connection.Query<TrustedCertificate>(
            "SELECT id, fingerprint, label, certificate_data, signer_name, added_date FROM trusted_certificates ORDER BY added_date DESC")
            .ToList();
    }

    public void Add(TrustedCertificate cert)
    {
        _connection.Execute(
            @"INSERT OR REPLACE INTO trusted_certificates (fingerprint, label, certificate_data, signer_name, added_date)
              VALUES (@Fingerprint, @Label, @CertificateData, @SignerName, @AddedDate)",
            new
            {
                cert.Fingerprint,
                cert.Label,
                cert.CertificateData,
                cert.SignerName,
                cert.AddedDate
            });
    }

    public void Remove(string fingerprint)
    {
        _connection.Execute(
            "DELETE FROM trusted_certificates WHERE fingerprint = @fingerprint",
            new { fingerprint });
    }
}
