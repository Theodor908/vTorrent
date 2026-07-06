using System;
using System.Security.Cryptography;

namespace vTorrent.Core.PeerCommunication.Identification
{
    /// <summary>
    /// Generates Azureus-style peer ID prefixes and full peer IDs.
    /// Mirrors libtorrent's fingerprint.cpp / generate_fingerprint().
    /// </summary>
    public static class ClientFingerprint
    {
        private const string AlphanumericChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>
        /// Generates an 8-character Azureus-style peer ID prefix.
        /// Format: -XX####- where XX is the 2-char client ID and #### are version digits.
        /// Version encoding: 0-9 → '0'-'9', 10-35 → 'A'-'Z' (matches libtorrent).
        /// </summary>
        public static string GeneratePrefix(string clientId, int major, int minor = 0,
                                             int revision = 0, int tag = 0)
        {
            if (clientId == null || clientId.Length != 2)
                throw new ArgumentOutOfRangeException(nameof(clientId), "Client ID must be exactly 2 characters");

            return string.Create(8, (clientId, major, minor, revision, tag), static (span, state) =>
            {
                span[0] = '-';
                span[1] = state.clientId[0];
                span[2] = state.clientId[1];
                span[3] = VersionToChar(state.major);
                span[4] = VersionToChar(state.minor);
                span[5] = VersionToChar(state.revision);
                span[6] = VersionToChar(state.tag);
                span[7] = '-';
            });
        }

        /// <summary>
        /// Generates a full 20-character peer ID: 8-char Azureus prefix + 12 random alphanumeric characters.
        /// </summary>
        public static string GeneratePeerId(string clientId, int major, int minor = 0,
                                             int revision = 0, int tag = 0)
        {
            return GeneratePeerIdFromPrefix(GeneratePrefix(clientId, major, minor, revision, tag));
        }

        /// <summary>
        /// Generates a full 20-character peer ID from an existing 8-char prefix
        /// by appending 12 random alphanumeric characters.
        /// </summary>
        public static string GeneratePeerIdFromPrefix(string prefix)
        {
            if (prefix == null || prefix.Length != 8)
                throw new ArgumentOutOfRangeException(nameof(prefix), "Prefix must be exactly 8 characters");

            return string.Create(20, prefix, static (span, pfx) =>
            {
                pfx.AsSpan().CopyTo(span);
                Span<byte> randomBytes = stackalloc byte[12];
                RandomNumberGenerator.Fill(randomBytes);
                for (int i = 0; i < 12; i++)
                {
                    span[8 + i] = AlphanumericChars[randomBytes[i] % AlphanumericChars.Length];
                }
            });
        }

        /// <summary>
        /// Encodes a version component (0-35) to a single ASCII character.
        /// 0-9 → '0'-'9', 10-35 → 'A'-'Z'.
        /// </summary>
        private static char VersionToChar(int v)
        {
            if (v < 0 || v > 35)
                throw new ArgumentOutOfRangeException(nameof(v), $"Version component must be 0-35, got {v}");

            return v < 10 ? (char)('0' + v) : (char)('A' + v - 10);
        }
    }
}
