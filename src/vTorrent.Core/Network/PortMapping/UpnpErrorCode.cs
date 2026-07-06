namespace vTorrent.Core.Network.PortMapping;

/// <summary>
/// UPnP IGD error codes per UPnP Device Architecture.
/// Matches libtorrent upnp_errors::error_code_enum.
/// </summary>
internal enum UpnpErrorCode
{
    NoError = 0,
    InvalidArgument = 402,
    ActionFailed = 501,
    ValueNotInArray = 714,
    SourceIpCannotBeWildcarded = 715,
    ExternalPortCannotBeWildcarded = 716,
    PortMappingConflict = 718,
    InternalPortMustMatchExternal = 724,
    OnlyPermanentLeasesSupported = 725,
    RemoteHostMustBeWildcard = 726,
    ExternalPortMustBeWildcard = 727,
}
