using System;

namespace vTorrent.Core.PeerCommunication.Identification
{
    /// <summary>
    /// Represents identified client software from a peer ID or extension handshake.
    /// </summary>
    public readonly record struct ClientSoftware(string Name, string Version)
    {
        public static ClientSoftware Unknown { get; } = new("Unknown", "");

        public override string ToString() =>
            string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}";
    }
}
