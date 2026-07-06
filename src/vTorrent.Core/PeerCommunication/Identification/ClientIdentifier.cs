using System;
using System.Collections.Generic;

namespace vTorrent.Core.PeerCommunication.Identification
{
    /// <summary>
    /// Identifies BitTorrent client software from a peer ID byte sequence.
    /// Supports Azureus-style, Shadow-style, and generic pattern matching,
    /// mirroring libtorrent's client detection logic.
    /// </summary>
    public static class ClientIdentifier
    {
        // ── Azureus lookup table (2-char code → client name) ──────────────────
        private static readonly Dictionary<string, string> AzureusClients =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "7T", "aTorrent" },
            { "AB", "AnyEvent BitTorrent" },
            { "AG", "Ares" },
            { "AR", "Arctic Torrent" },
            { "AT", "Artemis" },
            { "AV", "Avicora" },
            { "AX", "BitPump" },
            { "AZ", "Azureus" },
            { "A~", "Ares" },
            { "BB", "BitBuddy" },
            { "BC", "BitComet" },
            { "BE", "baretorrent" },
            { "BF", "Bitflu" },
            { "BG", "BTG" },
            { "BI", "BiglyBT" },
            { "BL", "BitBlinder" },
            { "BP", "BitTorrent Pro" },
            { "BR", "BitRocket" },
            { "BS", "BTSlave" },
            { "BT", "BitTorrent" },
            { "BU", "BigUp" },
            { "BW", "BitWombat" },
            { "BX", "BittorrentX" },
            { "CD", "Enhanced CTorrent" },
            { "CT", "CTorrent" },
            { "DE", "Deluge" },
            { "DP", "Propagate Data Client" },
            { "EB", "EBit" },
            { "ES", "electric sheep" },
            { "FC", "FileCroc" },
            { "FT", "FoxTorrent" },
            { "FW", "FrostWire" },
            { "FX", "Freebox BitTorrent" },
            { "GS", "GSTorrent" },
            { "HK", "Hekate" },
            { "HL", "Halite" },
            { "HN", "Hydranode" },
            { "IL", "iLivid" },
            { "KC", "Koinonein" },
            { "KG", "KGet" },
            { "KT", "KTorrent" },
            { "LC", "LeechCraft" },
            { "LH", "LH-ABC" },
            { "LK", "Linkage" },
            { "LP", "lphant" },
            { "LR", "LibreTorrent" },
            { "LT", "libtorrent" },
            { "LW", "Limewire" },
            { "ML", "MLDonkey" },
            { "MO", "Mono Torrent" },
            { "MP", "MooPolice" },
            { "MR", "Miro" },
            { "MT", "Moonlight Torrent" },
            { "NX", "Net Transport" },
            { "OS", "OneSwarm" },
            { "OT", "OmegaTorrent" },
            { "PD", "Pando" },
            { "QD", "QQDownload" },
            { "QT", "Qt 4" },
            { "RT", "Retriever" },
            { "RZ", "RezTorrent" },
            { "SB", "Swiftbit" },
            { "SD", "Xunlei" },
            { "SK", "spark" },
            { "SN", "ShareNet" },
            { "SS", "SwarmScope" },
            { "ST", "SymTorrent" },
            { "SZ", "Shareaza" },
            { "S~", "Shareaza" },
            { "TB", "Torch" },
            { "TL", "Tribler" },
            { "TN", "Torrent.NET" },
            { "TR", "Transmission" },
            { "TS", "TorrentStorm" },
            { "TT", "TuoTu" },
            { "UL", "uLeecher" },
            { "UM", "uTorrent Mac" },
            { "UT", "uTorrent" },
            { "VG", "Vagaa" },
            { "VT", "vTorrent" },
            { "WT", "BitLet" },
            { "WY", "FireTorrent" },
            { "XF", "Xfplay" },
            { "XL", "Xunlei" },
            { "XS", "XSwifter" },
            { "XT", "XanTorrent" },
            { "XX", "Xtorrent" },
            { "ZO", "Zona" },
            { "ZT", "ZipTorrent" },
            { "lt", "rTorrent" },
            { "pX", "pHoeniX" },
            { "qB", "qBittorrent" },
            { "st", "SharkTorrent" },
        };

        // ── Shadow-style client lookup (first byte char → client name) ─────────
        private static readonly Dictionary<char, string> ShadowClients =
            new Dictionary<char, string>
        {
            { 'A', "ABC" },
            { 'M', "Mainline" },
            { 'O', "Osprey Permaseed" },
            { 'Q', "BTQueue" },
            { 'R', "Tribler" },
            { 'S', "Shadow" },
            { 'T', "BitTornado" },
            { 'U', "UPnP NAT BitTorrent" },
        };

        // ── Generic pattern mappings (offset, pattern bytes, name) ────────────
        private static readonly (int Offset, string Pattern, string Name)[] GenericPatterns =
        {
            (0,  "Deadman Walking-",    "Deadman"),
            (5,  "Azureus",             "Azureus 2.0.3.2"),
            (0,  "DansClient",          "XanTorrent"),
            (4,  "btfans",              "SimpleBT"),
            (0,  "PRC.P---",            "Bittorrent Plus! II"),
            (0,  "P87.P---",            "Bittorrent Plus!"),
            (0,  "S587Plus",            "Bittorrent Plus!"),
            (0,  "martini",             "Martini Man"),
            (0,  "Plus---",             "Bittorrent Plus"),
            (0,  "turbobt",             "TurboBT"),
            (0,  "a00---0",             "Swarmy"),
            (0,  "a02---0",             "Swarmy"),
            (0,  "T00---0",             "Teeweety"),
            (0,  "BTDWV-",              "Deadman Walking"),
            (2,  "BS",                  "BitSpirit"),
            (0,  "-SP",                 "BitSpirit"),
            (0,  "Pando-",              "Pando"),
            (0,  "LIME",                "LimeWire"),
            (0,  "btuga",               "BTugaXP"),
            (0,  "oernu",               "BTugaXP"),
            (0,  "Mbrst",               "Burst!"),
            (0,  "PEERAPP",             "PeerApp"),
            (0,  "Plus",                "Plus!"),
            (0,  "-Qt-",                "Qt"),
            (0,  "exbc",                "BitComet"),
            (0,  "DNA",                 "BitTorrent DNA"),
            (0,  "-G3",                 "G3 Torrent"),
            (0,  "-FG",                 "FlashGet"),
            (0,  "-ML",                 "MLdonkey"),
            (0,  "-MG",                 "Media Get"),
            (0,  "XBT",                 "XBT"),
            (0,  "OP",                  "Opera"),
            (2,  "RS",                  "Rufus"),
            (0,  "AZ2500BT",            "BitTyrant"),
            (0,  "btpd/",               "BitTorrent Protocol Daemon"),
            (0,  "TIX",                 "Tixati"),
            (0,  "QVOD",                "Qvod"),
        };

        /// <summary>
        /// Identifies the client software from a 20-byte peer ID span.
        /// Returns <see cref="ClientSoftware.Unknown"/> for spans shorter than 20 bytes.
        /// </summary>
        public static ClientSoftware Identify(ReadOnlySpan<byte> peerId)
        {
            if (peerId.Length < 20)
                return ClientSoftware.Unknown;

            return TryParseAzureus(peerId)
                ?? TryParseShadow(peerId)
                ?? TryParseGeneric(peerId)
                ?? ClientSoftware.Unknown;
        }

        // ── Azureus-style: -XX####- ────────────────────────────────────────────
        private static ClientSoftware? TryParseAzureus(ReadOnlySpan<byte> peerId)
        {
            if (peerId[0] != '-' || peerId[7] != '-')
                return null;

            // Extract 2-char client code (bytes 1-2)
            char c1 = (char)peerId[1];
            char c2 = (char)peerId[2];

            // Basic sanity: both must be printable ASCII
            if (!IsAlphaNumOrSpecial(c1) || !IsAlphaNumOrSpecial(c2))
                return null;

            string code = new string(new[] { c1, c2 });

            // Decode version from bytes 3-6
            string version = DecodeAzureusVersion(peerId);

            if (AzureusClients.TryGetValue(code, out string? name))
                return new ClientSoftware(name, version);

            // Unknown Azureus code — return "Unknown (XX)"
            return new ClientSoftware($"Unknown ({code})", version);
        }

        // ── Shadow-style ──────────────────────────────────────────────────────
        private static ClientSoftware? TryParseShadow(ReadOnlySpan<byte> peerId)
        {
            char first = (char)peerId[0];
            if (!ShadowClients.TryGetValue(first, out string? name))
                return null;

            // Validate Shadow-style dash padding.
            // Classic Shadow uses dashes at positions 6, 7, 8 as separators/padding.
            // Some variants use dashes at positions 4 and 5 instead.
            bool hasDashAt678 = peerId[6] == '-' && peerId[7] == '-' && peerId[8] == '-';
            bool hasDashAt45  = peerId[4] == '-' && peerId[5] == '-';

            if (!hasDashAt678 && !hasDashAt45)
                return null;

            string version = DecodeShadowVersion(peerId);
            return new ClientSoftware(name, version);
        }

        // ── Generic patterns ──────────────────────────────────────────────────
        private static ClientSoftware? TryParseGeneric(ReadOnlySpan<byte> peerId)
        {
            foreach (var (offset, pattern, clientName) in GenericPatterns)
            {
                if (offset + pattern.Length > peerId.Length)
                    continue;

                bool match = true;
                for (int i = 0; i < pattern.Length; i++)
                {
                    if (peerId[offset + i] != (byte)pattern[i])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return new ClientSoftware(clientName, "");
            }

            return null;
        }

        // ── Version decoding ──────────────────────────────────────────────────

        /// <summary>
        /// Decodes the 4-digit Azureus version from bytes 3-6.
        /// Each byte is decoded by <see cref="DecodeDigit"/> and formatted as "a.b.c.d".
        /// </summary>
        private static string DecodeAzureusVersion(ReadOnlySpan<byte> peerId)
        {
            int a = DecodeDigit((char)peerId[3]);
            int b = DecodeDigit((char)peerId[4]);
            int c = DecodeDigit((char)peerId[5]);
            int d = DecodeDigit((char)peerId[6]);
            return $"{a}.{b}.{c}.{d}";
        }

        /// <summary>
        /// Decodes the Shadow-style version from bytes 1-5.
        /// Each byte is decoded by <see cref="DecodeDigit"/>, trailing zeros are trimmed.
        /// </summary>
        private static string DecodeShadowVersion(ReadOnlySpan<byte> peerId)
        {
            // Shadow version bytes are at positions 1-5 (up to 5 components)
            int[] parts = new int[5];
            for (int i = 0; i < 5; i++)
                parts[i] = DecodeDigit((char)peerId[1 + i]);

            // Trim trailing zeros from the right
            int last = parts.Length - 1;
            while (last > 0 && parts[last] == 0)
                last--;

            return string.Join(".", parts[0..(last + 1)]);
        }

        /// <summary>
        /// Decodes a single Azureus/Shadow version digit character.
        /// '0'-'9' → 0-9, 'A'-'Z' → 10-35, 'a'-'z' → 10-35, else 0.
        /// </summary>
        private static int DecodeDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            if (c >= 'a' && c <= 'z') return c - 'a' + 10;
            return 0;
        }

        /// <summary>
        /// Returns true if the character is alphanumeric or a common special char
        /// used in Azureus client codes (e.g. '~').
        /// </summary>
        private static bool IsAlphaNumOrSpecial(char c)
        {
            return (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '~';
        }
    }
}
