using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;

namespace vTorrent.Core.DHT;

/// <summary>
/// Manages write tokens for announce_peer authentication.
/// Matches libtorrent's token format: 4 bytes from SHA1(IP_string || secret || info_hash).
/// </summary>
internal class TokenManager
{
    /// <summary>
    /// Token size in bytes - libtorrent uses exactly 4 bytes.
    /// </summary>
    private const int TokenSize = 4;

    private readonly TimeSpan _tokenLifetime;
    private byte[] _currentSecret;
    private byte[] _previousSecret;
    private DateTime _lastRotation;

    public TokenManager(TimeSpan tokenLifetime)
    {
        _tokenLifetime = tokenLifetime;
        _currentSecret = GenerateSecret();
        _previousSecret = GenerateSecret();
        _lastRotation = DateTime.UtcNow;
    }

    private static byte[] GenerateSecret()
    {
        var secret = new byte[16];
        RandomNumberGenerator.Fill(secret);
        return secret;
    }

    /// <summary>
    /// Generates a 4-byte token for announce_peer verification.
    /// Format matches libtorrent: first 4 bytes of SHA1(IP_string || secret || info_hash).
    /// </summary>
    public byte[] GenerateToken(IPAddress ip, byte[] infoHash)
    {
        MaybeRotate();
        return GenerateTokenWithSecret(_currentSecret, ip, infoHash);
    }

    /// <summary>
    /// Validates a token against current and previous secrets.
    /// </summary>
    public bool ValidateToken(byte[] token, IPAddress ip, byte[] infoHash)
    {
        if (token == null || token.Length != TokenSize)
            return false;

        MaybeRotate();

        // Check against current and previous secrets
        var currentToken = GenerateTokenWithSecret(_currentSecret, ip, infoHash);
        if (token.SequenceEqual(currentToken))
            return true;

        var previousToken = GenerateTokenWithSecret(_previousSecret, ip, infoHash);
        return token.SequenceEqual(previousToken);
    }

    /// <summary>
    /// Generates a 4-byte token using the specified secret.
    /// libtorrent format: SHA1(IP_string || secret || info_hash), take first 4 bytes.
    /// </summary>
    private byte[] GenerateTokenWithSecret(byte[] secret, IPAddress ip, byte[] infoHash)
    {
        using var sha1 = SHA1.Create();

        // libtorrent uses IP as string (addr.to_string())
        string ipString = ip.ToString();
        var ipBytes = System.Text.Encoding.ASCII.GetBytes(ipString);

        // Build input: IP_string || secret || info_hash
        // This matches libtorrent's order in node::generate_token
        var input = new byte[ipBytes.Length + secret.Length + (infoHash?.Length ?? 0)];
        ipBytes.CopyTo(input, 0);
        secret.CopyTo(input, ipBytes.Length);
        infoHash?.CopyTo(input, ipBytes.Length + secret.Length);

        var hash = sha1.ComputeHash(input);

        // Return only first 4 bytes (libtorrent: write_token_size = 4)
        var token = new byte[TokenSize];
        Array.Copy(hash, token, TokenSize);
        return token;
    }

    private void MaybeRotate()
    {
        if ((DateTime.UtcNow - _lastRotation) > _tokenLifetime)
        {
            _previousSecret = _currentSecret;
            _currentSecret = GenerateSecret();
            _lastRotation = DateTime.UtcNow;
        }
    }
}
