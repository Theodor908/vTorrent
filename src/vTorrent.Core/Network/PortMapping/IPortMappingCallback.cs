namespace vTorrent.Core.Network.PortMapping;

public interface IPortMappingCallback
{
    void OnPortMapped(PortMapping mapping);
    void OnPortMapError(PortMapping mapping, string error);
}
