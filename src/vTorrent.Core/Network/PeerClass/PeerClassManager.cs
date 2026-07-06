using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.PeerClass;

public sealed class PeerClassManager
{
    private readonly Dictionary<int, PeerClass> _classes = new();
    private readonly PeerClassFilter _filter = new();
    private int _nextId = 1;

    public PeerClassManager()
    {
        _classes[0] = new PeerClass(0, "Default");
    }

    public PeerClass CreateClass(string name, int uploadLimit = 0, int downloadLimit = 0)
    {
        var id = _nextId++;
        var cls = new PeerClass(id, name, uploadLimit, downloadLimit);
        _classes[id] = cls;
        return cls;
    }

    public void RemoveClass(int classId)
    {
        if (classId == 0) return;
        _classes.Remove(classId);
    }

    public void SetFilter(IPAddress first, IPAddress last, int classId)
        => _filter.AddRule(first, last, classId);

    public void SetFilterFromCidr(string cidr, int classId)
        => _filter.AddRuleFromCidr(cidr, classId);

    public PeerClass Classify(IPAddress peerAddress)
    {
        if (IPAddress.None.Equals(peerAddress))
            return _classes[0]; // I2P / unknown -> default

        var classId = _filter.Classify(peerAddress);
        return _classes.TryGetValue(classId, out var cls) ? cls : _classes[0];
    }

    public IReadOnlyList<PeerClass> GetAllClasses() => _classes.Values.ToList();

    public void LoadFromSettings(PeerClassSettings settings)
    {
        if (!settings.Enabled) return;
        foreach (var def in settings.Classes)
        {
            var cls = CreateClass(def.Name, def.UploadLimitBytesPerSec, def.DownloadLimitBytesPerSec);
            foreach (var cidr in def.IpRanges)
                SetFilterFromCidr(cidr, cls.Id);
        }
    }
}
