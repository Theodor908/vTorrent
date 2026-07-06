using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace vTorrent.Core.Network.PeerClass;

public sealed class PeerClassFilter
{
    private readonly SortedList<uint, int> _v4 = new() { { 0, 0 } };

    public int Classify(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return 0;
        return FloorLookup(_v4, IpToUInt32(address));
    }

    public void AddRule(IPAddress first, IPAddress last, int classId)
    {
        if (first.AddressFamily != AddressFamily.InterNetwork ||
            last.AddressFamily != AddressFamily.InterNetwork) return;

        var firstKey = IpToUInt32(first);
        var lastKey = IpToUInt32(last);
        if (firstKey > lastKey) return;

        var previousClassId = FloorLookup(_v4, lastKey < uint.MaxValue ? lastKey + 1 : lastKey);
        var classBefore = FloorLookup(_v4, firstKey);

        var keysToRemove = new List<uint>();
        foreach (var kvp in _v4)
        {
            if (kvp.Key > lastKey) break;
            if (kvp.Key >= firstKey && kvp.Key <= lastKey)
                keysToRemove.Add(kvp.Key);
        }
        foreach (var k in keysToRemove) _v4.Remove(k);

        _v4[firstKey] = classId;
        if (lastKey < uint.MaxValue)
            _v4[lastKey + 1] = previousClassId;

        if (classId == classBefore) _v4.Remove(firstKey);
        if (lastKey < uint.MaxValue && classId == previousClassId) _v4.Remove(lastKey + 1);
    }

    public void AddRuleFromCidr(string cidr, int classId)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp) ||
            !int.TryParse(parts[1], out var prefixLen) || prefixLen < 0 || prefixLen > 32) return;

        var baseVal = IpToUInt32(baseIp);
        var mask = prefixLen == 0 ? 0u : uint.MaxValue << (32 - prefixLen);
        AddRule(UInt32ToIp(baseVal & mask), UInt32ToIp((baseVal & mask) | ~mask), classId);
    }

    private static int FloorLookup(SortedList<uint, int> list, uint key)
    {
        var keys = list.Keys;
        int lo = 0, hi = keys.Count - 1, result = 0;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (keys[mid] <= key) { result = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return list.Values[result];
    }

    private static uint IpToUInt32(IPAddress ip) =>
        BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());

    private static IPAddress UInt32ToIp(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
