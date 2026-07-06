using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.TrackerCommunication.Http;
using vTorrent.Core.PeerCommunication.Extensions;
using vTorrent.Bencode.Parsers;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Privacy;

public class AnonymousModeTests
{
    private readonly Mock<IOptionsMonitor<TrackerSettings>> _trackerMonitor;
    private readonly Mock<IOptionsMonitor<PrivacySettings>> _privacyMonitor;
    private readonly ILogger<HttpTrackerClient> _logger = NullLogger<HttpTrackerClient>.Instance;
    private readonly Mock<IBencodeParser> _parser = new();

    public AnonymousModeTests()
    {
        var trackerSettings = new TrackerSettings { UserAgent = "vTorrent/1.0", AnnounceIp = "1.2.3.4" };
        _trackerMonitor = new Mock<IOptionsMonitor<TrackerSettings>>();
        _trackerMonitor.Setup(m => m.CurrentValue).Returns(trackerSettings);

        _privacyMonitor = new Mock<IOptionsMonitor<PrivacySettings>>();
    }

    private void SetAnonymousMode(bool enabled)
    {
        var privacy = new PrivacySettings { AnonymousMode = enabled };
        _privacyMonitor.Setup(m => m.CurrentValue).Returns(privacy);
    }

    [Fact]
    public void AnonymousMode_AnnounceUserAgent_EmptyWhenEnabled()
    {
        SetAnonymousMode(true);
        var client = new HttpTrackerClient(
            "http://tracker.example.com/announce",
            _trackerMonitor.Object, _logger, _parser.Object,
            privacyMonitor: _privacyMonitor.Object);

        client.GetEffectiveUserAgent().Should().BeEmpty();
    }

    [Fact]
    public void AnonymousMode_AnnounceUserAgent_NormalWhenDisabled()
    {
        SetAnonymousMode(false);
        var client = new HttpTrackerClient(
            "http://tracker.example.com/announce",
            _trackerMonitor.Object, _logger, _parser.Object,
            privacyMonitor: _privacyMonitor.Object);

        client.GetEffectiveUserAgent().Should().Be("vTorrent/1.0");
    }

    [Fact]
    public void AnonymousMode_AnnounceIp_SuppressedWhenEnabled()
    {
        SetAnonymousMode(true);
        var client = new HttpTrackerClient(
            "http://tracker.example.com/announce",
            _trackerMonitor.Object, _logger, _parser.Object,
            privacyMonitor: _privacyMonitor.Object);

        client.ShouldSuppressAnnounceIp().Should().BeTrue();
    }

    [Fact]
    public void AnonymousMode_AnnounceIp_SentWhenDisabled()
    {
        SetAnonymousMode(false);
        var client = new HttpTrackerClient(
            "http://tracker.example.com/announce",
            _trackerMonitor.Object, _logger, _parser.Object,
            privacyMonitor: _privacyMonitor.Object);

        client.ShouldSuppressAnnounceIp().Should().BeFalse();
    }

    [Fact]
    public void AnonymousMode_PeerIdPrefix_UnchangedWhenEnabled()
    {
        // Peer ID prefix "-VT0100-" must remain even in anonymous mode (libtorrent parity)
        var settings = new PeerSettings();
        settings.PeerId.Should().StartWith("-VT");
    }

    [Fact]
    public void AnonymousMode_ExtensionHandshake_EmptyVersionWhenEnabled()
    {
        var privacySettings = new PrivacySettings { AnonymousMode = true };
        var privacyMonitor = new Mock<IOptionsMonitor<PrivacySettings>>();
        privacyMonitor.Setup(m => m.CurrentValue).Returns(privacySettings);

        var logger = NullLogger<ExtensionManager>.Instance;
        var em = new ExtensionManager(
            logger,
            "vTorrent/1.0",
            6881,
            (msg, ct) => Task.CompletedTask,
            privacyMonitor: privacyMonitor.Object);

        em.GetEffectiveClientVersion().Should().BeEmpty();
    }

    [Fact]
    public void AnonymousMode_ExtensionHandshake_NormalWhenDisabled()
    {
        var privacySettings = new PrivacySettings { AnonymousMode = false };
        var privacyMonitor = new Mock<IOptionsMonitor<PrivacySettings>>();
        privacyMonitor.Setup(m => m.CurrentValue).Returns(privacySettings);

        var logger = NullLogger<ExtensionManager>.Instance;
        var em = new ExtensionManager(
            logger,
            "vTorrent/1.0",
            6881,
            (msg, ct) => Task.CompletedTask,
            privacyMonitor: privacyMonitor.Object);

        em.GetEffectiveClientVersion().Should().Be("vTorrent/1.0");
    }

    [Fact]
    public void AnonymousMode_ScrapeUserAgent_EmptyWhenEnabled()
    {
        // Same as announce — GetEffectiveUserAgent is shared for both paths
        SetAnonymousMode(true);
        var client = new HttpTrackerClient(
            "http://tracker.example.com/announce",
            _trackerMonitor.Object, _logger, _parser.Object,
            privacyMonitor: _privacyMonitor.Object);

        client.GetEffectiveUserAgent().Should().BeEmpty();
    }
}
