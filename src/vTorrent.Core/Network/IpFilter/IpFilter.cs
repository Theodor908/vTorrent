using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace vTorrent.Core.Network.IpFilter;

public sealed class IpFilter
{
    private readonly SortedList<uint, AccessFlags> _v4 = new() { { 0, AccessFlags.Allowed } };

    public AccessFlags Access(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var key = IpToUInt32(address);
            return FloorLookup(_v4, key);
        }
        return AccessFlags.Allowed;
    }

    public void AddRule(IPAddress first, IPAddress last, AccessFlags flags)
    {
        if (first.AddressFamily != AddressFamily.InterNetwork ||
            last.AddressFamily != AddressFamily.InterNetwork)
            return;

        var firstKey = IpToUInt32(first);
        var lastKey = IpToUInt32(last);
        if (firstKey > lastKey) return;

        var previousFlags = FloorLookup(_v4, lastKey < uint.MaxValue ? lastKey + 1 : lastKey);
        var flagsBefore = FloorLookup(_v4, firstKey);

        var keysToRemove = new List<uint>();
        foreach (var kvp in _v4)
        {
            if (kvp.Key > lastKey) break;
            if (kvp.Key >= firstKey && kvp.Key <= lastKey)
                keysToRemove.Add(kvp.Key);
        }
        foreach (var k in keysToRemove)
            _v4.Remove(k);

        _v4[firstKey] = flags;
        if (lastKey < uint.MaxValue)
            _v4[lastKey + 1] = previousFlags;

        if (flags == flagsBefore)
            _v4.Remove(firstKey);

        if (lastKey < uint.MaxValue && flags == previousFlags)
            _v4.Remove(lastKey + 1);
    }

    public void AddRuleFromCidr(string cidr, AccessFlags flags)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) ||
            !int.TryParse(parts[1], out var prefixLen) || prefixLen < 0 || prefixLen > 32)
            return;

        var baseVal = IpToUInt32(baseIp);
        var mask = prefixLen == 0 ? 0u : uint.MaxValue << (32 - prefixLen);
        var first = baseVal & mask;
        var last = first | ~mask;

        AddRule(UInt32ToIp(first), UInt32ToIp(last), flags);
    }

    public int BoundaryCount => _v4.Count;

    private static AccessFlags FloorLookup(SortedList<uint, AccessFlags> list, uint key)
    {
        var keys = list.Keys;
        int lo = 0, hi = keys.Count - 1;
        int result = 0;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (keys[mid] <= key) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return list.Values[result];
    }

    private static uint IpToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress UInt32ToIp(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
