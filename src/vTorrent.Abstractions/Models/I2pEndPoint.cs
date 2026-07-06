using System;
using System.Net;
using System.Net.Sockets;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// EndPoint subclass wrapping an I2P destination.
/// Allows I2P addresses to flow through .NET's EndPoint-based APIs.
/// </summary>
public sealed class I2pEndPoint : EndPoint, IEquatable<I2pEndPoint>
{
    // Use an unused AddressFamily value for I2P
    private const AddressFamily I2pAddressFamily = (AddressFamily)99;

    public I2pDestination Destination { get; }

    public I2pEndPoint(I2pDestination destination)
    {
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
    }

    public override AddressFamily AddressFamily => I2pAddressFamily;

    public override string ToString() => $"i2p:{Destination}";

    public bool Equals(I2pEndPoint? other)
    {
        if (other is null) return false;
        return Destination.Equals(other.Destination);
    }

    public override bool Equals(object? obj) => Equals(obj as I2pEndPoint);

    public override int GetHashCode() => Destination.GetHashCode();
}
