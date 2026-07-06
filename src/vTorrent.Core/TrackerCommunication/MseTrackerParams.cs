using System.Collections.Generic;
using vTorrent.Core.Settings;
using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Core.TrackerCommunication;

/// <summary>
/// Adds BEP 8 encryption-related parameters to tracker announce requests.
/// </summary>
public static class MseTrackerParams
{
    public static void Apply(
        IDictionary<string, string> queryParams,
        byte[] infoHash,
        EncryptionSettings settings)
    {
        bool anyEncryption = settings.OutPolicy != EncryptionPolicy.Disabled
                          || settings.InPolicy != EncryptionPolicy.Disabled;

        if (!anyEncryption)
            return;

        queryParams["supportcrypto"] = "1";

        if (settings.OutPolicy == EncryptionPolicy.Forced
            && settings.InPolicy == EncryptionPolicy.Forced)
        {
            queryParams["requirecrypto"] = "1";
        }
    }
}
