using System;

namespace vTorrent.Core.PeerCommunication.Encryption.Primitives;

/// <summary>
/// RC4 stream cipher (ARC4). Used for MSE/PE and BEP 8.
/// Not cryptographically secure — used for protocol obfuscation only.
/// </summary>
public sealed class RC4 : IDisposable
{
    private readonly byte[] _s = new byte[256];
    private byte _i, _j;

    public RC4(ReadOnlySpan<byte> key)
    {
        for (int k = 0; k < 256; k++)
            _s[k] = (byte)k;

        byte j = 0;
        for (int k = 0; k < 256; k++)
        {
            j = (byte)(j + _s[k] + key[k % key.Length]);
            (_s[k], _s[j]) = (_s[j], _s[k]);
        }
    }

    public void Process(Span<byte> data)
    {
        for (int n = 0; n < data.Length; n++)
        {
            _i = (byte)(_i + 1);
            _j = (byte)(_j + _s[_i]);
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            data[n] ^= _s[(byte)(_s[_i] + _s[_j])];
        }
    }

    public void Discard(int count)
    {
        for (int n = 0; n < count; n++)
        {
            _i = (byte)(_i + 1);
            _j = (byte)(_j + _s[_i]);
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
        }
    }

    public void Dispose()
    {
        Array.Clear(_s);
        _i = 0;
        _j = 0;
    }
}
