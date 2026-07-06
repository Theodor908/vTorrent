namespace vTorrent.Core.PeerCommunication.Extensions;

/// <summary>
/// Determines which PEX extension to register for a given peer based on
/// torrent type, peer type, and mixed mode settings.
/// </summary>
public static class PexRegistrationHelper
{
    /// <summary>
    /// Returns the PEX extension name to register, or null if the peer should be rejected.
    /// </summary>
    public static string? GetPexExtensionName(bool isI2pTorrent, bool isI2pPeer, bool allowMixedMode)
    {
        if (!isI2pTorrent)
            return "ut_pex";

        if (isI2pPeer)
            return I2pPexExtension.Name; // "i2p_pex"

        if (allowMixedMode)
            return "ut_pex";

        return null; // Pure I2P, clearnet peer, no mixed → reject
    }
}
