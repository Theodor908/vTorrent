using FluentAssertions;
using System.Net;
using Xunit;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.PeerClass;

namespace vTorrent.Core.Tests.Network.PeerClass;

public class PeerClassManagerTests
{
    [Fact]
    public void DefaultClass_AlwaysExists()
    {
        var manager = new PeerClassManager();
        var defaultClass = manager.Classify(IPAddress.Parse("1.2.3.4"));
        defaultClass.Should().NotBeNull();
        defaultClass.Name.Should().Be("Default");
        defaultClass.Id.Should().Be(0);
    }

    [Fact]
    public void CreateClass_ReturnsNewId()
    {
        var manager = new PeerClassManager();
        var cls = manager.CreateClass("Local Network", uploadLimit: 0, downloadLimit: 0);
        cls.Id.Should().BeGreaterThan(0);
        cls.Name.Should().Be("Local Network");
    }

    [Fact]
    public void SetFilter_ClassifiesCorrectly()
    {
        var manager = new PeerClassManager();
        var localClass = manager.CreateClass("Local", uploadLimit: 0);
        manager.SetFilterFromCidr("192.168.0.0/16", localClass.Id);
        manager.Classify(IPAddress.Parse("192.168.1.100")).Id.Should().Be(localClass.Id);
        manager.Classify(IPAddress.Parse("192.168.1.100")).Name.Should().Be("Local");
    }

    [Fact]
    public void Classify_UnmatchedIp_ReturnsDefault()
    {
        var manager = new PeerClassManager();
        manager.CreateClass("Local");
        manager.SetFilterFromCidr("192.168.0.0/16", 1);
        manager.Classify(IPAddress.Parse("8.8.8.8")).Id.Should().Be(0);
    }

    [Fact]
    public void I2pPeer_ReturnsDefault()
    {
        var manager = new PeerClassManager();
        manager.CreateClass("Throttled", uploadLimit: 1024);
        manager.SetFilterFromCidr("0.0.0.0/0", 1);
        manager.Classify(IPAddress.None).Id.Should().Be(0);
    }

    [Fact]
    public void ClassChannels_HaveCorrectLimits()
    {
        var manager = new PeerClassManager();
        var cls = manager.CreateClass("Throttled", uploadLimit: 51200, downloadLimit: 102400);
        cls.UploadChannel.Should().NotBeNull();
        cls.DownloadChannel.Should().NotBeNull();
        cls.UploadChannel.Throttle.Should().Be(51200);
        cls.DownloadChannel.Throttle.Should().Be(102400);
    }

    [Fact]
    public void LoadFromSettings_PopulatesClasses()
    {
        var settings = new PeerClassSettings
        {
            Enabled = true,
            Classes = new()
            {
                new PeerClassDefinition
                {
                    Name = "Local",
                    UploadLimitBytesPerSec = 0,
                    DownloadLimitBytesPerSec = 0,
                    IpRanges = new() { "192.168.0.0/16", "10.0.0.0/8" }
                },
                new PeerClassDefinition
                {
                    Name = "Throttled",
                    UploadLimitBytesPerSec = 51200,
                    DownloadLimitBytesPerSec = 0,
                    IpRanges = new() { "172.16.0.0/12" }
                }
            }
        };

        var manager = new PeerClassManager();
        manager.LoadFromSettings(settings);
        manager.GetAllClasses().Should().HaveCount(3);
        manager.Classify(IPAddress.Parse("192.168.1.1")).Name.Should().Be("Local");
        manager.Classify(IPAddress.Parse("10.0.0.1")).Name.Should().Be("Local");
        manager.Classify(IPAddress.Parse("172.16.0.1")).Name.Should().Be("Throttled");
        manager.Classify(IPAddress.Parse("8.8.8.8")).Name.Should().Be("Default");
    }

    [Fact]
    public void RemoveClass_FallsBackToDefault()
    {
        var manager = new PeerClassManager();
        var cls = manager.CreateClass("Temp");
        manager.SetFilterFromCidr("10.0.0.0/8", cls.Id);
        manager.Classify(IPAddress.Parse("10.0.0.1")).Id.Should().Be(cls.Id);
        manager.RemoveClass(cls.Id);
        manager.Classify(IPAddress.Parse("10.0.0.1")).Id.Should().Be(0);
    }
}
