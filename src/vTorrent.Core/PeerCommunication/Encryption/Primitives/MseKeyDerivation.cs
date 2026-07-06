using System;
using System.Security.Cryptography;
using System.Text;

namespace vTorrent.Core.PeerCommunication.Encryption.Primitives;

/// <summary>
/// SHA1-based key derivation functions for MSE/PE handshake.
/// </summary>
public static class MseKeyDerivation
{
    public static byte[] Hash(string prefix, byte[] data)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        var input = new byte[prefixBytes.Length + data.Length];
        prefixBytes.CopyTo(input, 0);
        data.CopyTo(input, prefixBytes.Length);
        return SHA1.HashData(input);
    }

    public static byte[] Hash(string prefix, byte[] S, byte[] skey)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        var input = new byte[prefixBytes.Length + S.Length + skey.Length];
        prefixBytes.CopyTo(input, 0);
        S.CopyTo(input, prefixBytes.Length);
        skey.CopyTo(input, prefixBytes.Length + S.Length);
        return SHA1.HashData(input);
    }

    public static byte[] ComputeReq2Hash(byte[] infoHash) => Hash("req2", infoHash);

    public static byte[] ComputeTrackerObfuscatedHash(byte[] infoHash) => SHA1.HashData(infoHash);

    public static (RC4 outgoing, RC4 incoming) CreateRC4Pair(byte[] S, byte[] skey)
    {
        var keyA = Hash("keyA", S, skey);
        var keyB = Hash("keyB", S, skey);

        var outgoing = new RC4(keyA);
        outgoing.Discard(1024);

        var incoming = new RC4(keyB);
        incoming.Discard(1024);

        return (outgoing, incoming);
    }
}
