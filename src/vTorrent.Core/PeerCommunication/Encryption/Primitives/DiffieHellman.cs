using System;
using System.Numerics;
using System.Security.Cryptography;

namespace vTorrent.Core.PeerCommunication.Encryption.Primitives;

/// <summary>
/// 768-bit Diffie-Hellman key exchange using the MSE-specified prime.
/// </summary>
public sealed class DiffieHellman
{
    private const int KeyLength = 96;

    private static readonly BigInteger P = BigInteger.Parse(
        "0" +
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A63A36210000000000090563",
        System.Globalization.NumberStyles.HexNumber);

    private static readonly BigInteger G = 2;

    private readonly BigInteger _privateKey;

    public byte[] PublicKey { get; }

    public DiffieHellman()
    {
        var privateBytes = RandomNumberGenerator.GetBytes(20);
        _privateKey = new BigInteger(privateBytes, isUnsigned: true, isBigEndian: true);

        var pub = BigInteger.ModPow(G, _privateKey, P);
        PublicKey = ToBigEndian96(pub);
    }

    public byte[] ComputeSharedSecret(ReadOnlySpan<byte> remotePublicKey)
    {
        var remotePub = new BigInteger(remotePublicKey, isUnsigned: true, isBigEndian: true);
        var secret = BigInteger.ModPow(remotePub, _privateKey, P);
        return ToBigEndian96(secret);
    }

    private static byte[] ToBigEndian96(BigInteger value)
    {
        var result = new byte[KeyLength];
        value.TryWriteBytes(result, out int bytesWritten, isUnsigned: true, isBigEndian: true);

        if (bytesWritten < KeyLength)
        {
            var padded = new byte[KeyLength];
            result.AsSpan(0, bytesWritten).CopyTo(padded.AsSpan(KeyLength - bytesWritten));
            return padded;
        }

        return result;
    }
}
