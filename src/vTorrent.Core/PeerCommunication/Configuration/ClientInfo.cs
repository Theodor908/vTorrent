using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication.Identification;

namespace vTorrent.Core.PeerCommunication.Configuration
{
    public static class ClientInfo
    {
        /// <summary>
        /// Client name.
        /// </summary>
        public const string Name = "vTorrent";

        /// <summary>
        /// Client version.
        /// </summary>
        public const string Version = "1.0.0";

        /// <summary>
        /// Full user agent string for HTTP tracker requests.
        /// </summary>
        public const string UserAgent = "vTorrent/1.0.0";

        /// <summary>
        /// Client peer ID prefix following BitTorrent conventions.
        /// Format: -XX####- where XX is client code, #### is version.
        /// VT = vTorrent, 0100 = version 1.0.0
        /// </summary>
        public static string PeerIdPrefix { get; } =
            ClientFingerprint.GeneratePrefix("VT", 1, 0, 0);

        /// <summary>
        /// Client version string for extension handshakes.
        /// Format: "ClientName/Version"
        /// </summary>
        public const string ClientVersionString = Name + "/" + Version;
    }
}
