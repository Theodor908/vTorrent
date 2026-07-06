// tests/vTorrent.Core.Tests/Network/I2P/I2pTrackerClientTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using vTorrent.Abstractions.Settings;
using vTorrent.Bencode.Parsers;
using vTorrent.Core.Network.I2P;
using vTorrent.Core.TrackerCommunication;
using vTorrent.Core.TrackerCommunication.I2P;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.Tests.Network.I2P;

public class I2pTrackerClientTests
{
    [Fact]
    public void GetProtocol_I2pHttpUrl_ReturnsI2p()
    {
        var protocol = TrackerClientFactory.GetProtocol("http://tracker.example.i2p/announce");
        protocol.Should().Be(TrackerProtocol.I2p);
    }

    [Fact]
    public void GetProtocol_I2pHttpsUrl_ReturnsI2p()
    {
        var protocol = TrackerClientFactory.GetProtocol("https://tracker.example.i2p/announce");
        protocol.Should().Be(TrackerProtocol.I2p);
    }

    [Fact]
    public void GetProtocol_I2pNakedDomain_ReturnsI2p()
    {
        var protocol = TrackerClientFactory.GetProtocol("http://tracker.i2p");
        protocol.Should().Be(TrackerProtocol.I2p);
    }

    [Fact]
    public void GetProtocol_RegularHttp_ReturnsHttp()
    {
        var protocol = TrackerClientFactory.GetProtocol("http://tracker.example.com/announce");
        protocol.Should().Be(TrackerProtocol.Http);
    }

    [Fact]
    public void GetProtocol_RegularUdp_ReturnsUdp()
    {
        var protocol = TrackerClientFactory.GetProtocol("udp://tracker.example.com:6969/announce");
        protocol.Should().Be(TrackerProtocol.Udp);
    }

    [Fact]
    public void IsSupported_I2pUrl_ReturnsTrue()
    {
        var factory = CreateFactory();
        factory.IsSupported("http://tracker.example.i2p/announce").Should().BeTrue();
    }

    [Fact]
    public void I2pDetection_TakesPriorityOverHttps()
    {
        var protocol = TrackerClientFactory.GetProtocol("https://tracker.example.i2p/announce");
        protocol.Should().Be(TrackerProtocol.I2p);
        protocol.Should().NotBe(TrackerProtocol.Https);
    }

    [Fact]
    public void CreateClient_I2pUrl_WithoutSession_Throws()
    {
        var factory = CreateFactory();
        var act = () => factory.CreateClient("http://tracker.example.i2p/announce");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*I2P service*");
    }

    [Fact]
    public void I2pHttpTrackerClient_Constructor_NullUrl_Throws()
    {
        var i2pService = CreateMockI2pService();
        var act = () => new I2pHttpTrackerClient(null!, i2pService);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void I2pHttpTrackerClient_Constructor_NullSession_Throws()
    {
        var act = () => new I2pHttpTrackerClient("http://tracker.i2p/announce", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void I2pHttpTrackerClient_Type_ReturnsHttp()
    {
        var client = new I2pHttpTrackerClient("http://tracker.i2p/announce", CreateMockI2pService());
        client.Type.Should().Be(TrackerType.Http);
    }

    [Fact]
    public void I2pHttpTrackerClient_IsAvailable_InitiallyTrue()
    {
        var client = new I2pHttpTrackerClient("http://tracker.i2p/announce", CreateMockI2pService());
        client.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void I2pHttpTrackerClient_TrackerUrl_ReturnsConfigured()
    {
        const string url = "http://tracker.example.i2p/announce";
        var client = new I2pHttpTrackerClient(url, CreateMockI2pService());
        client.TrackerUrl.Should().Be(url);
    }

    [Fact]
    public void I2pHttpTrackerClient_ScrapeAsync_ThrowsNotSupported()
    {
        var client = new I2pHttpTrackerClient("http://tracker.i2p/announce", CreateMockI2pService());
        var act = async () => await client.ScrapeAsync(new byte[20]);
        act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void I2pHttpTrackerClient_BuildAnnounceQuery_ContainsRequiredParams()
    {
        var client = new I2pHttpTrackerClient("http://tracker.i2p/announce", CreateMockI2pService());
        var request = TrackerRequest.CreateStarted(new byte[20], new byte[20], 6881, 1000);
        var query = client.BuildAnnounceQueryForTest(request, "/announce");
        query.Should().Contain("info_hash=");
        query.Should().Contain("peer_id=");
        query.Should().Contain("port=6881");
        query.Should().Contain("uploaded=0");
        query.Should().Contain("downloaded=0");
        query.Should().Contain("left=1000");
        query.Should().Contain("compact=1");
        query.Should().Contain("event=started");
    }

    [Fact]
    public void I2pHttpTrackerClient_ExtractHttpBody_FindsBody()
    {
        var response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nBodyContent"u8.ToArray();
        var body = I2pHttpTrackerClient.ExtractHttpBodyForTest(response);
        System.Text.Encoding.ASCII.GetString(body).Should().Be("BodyContent");
    }

    [Fact]
    public void I2pHttpTrackerClient_ExtractHttpBody_NoSeparator_ReturnsAll()
    {
        var response = "RawData"u8.ToArray();
        var body = I2pHttpTrackerClient.ExtractHttpBodyForTest(response);
        System.Text.Encoding.ASCII.GetString(body).Should().Be("RawData");
    }

    private static TrackerClientFactory CreateFactory()
    {
        var trackerMonitor = new Mock<IOptionsMonitor<TrackerSettings>>();
        trackerMonitor.Setup(m => m.CurrentValue).Returns(new TrackerSettings());
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
        var bencodeParser = new Mock<IBencodeParser>();
        return new TrackerClientFactory(trackerMonitor.Object, loggerFactory.Object, bencodeParser.Object);
    }

    private static I2pService CreateMockI2pService()
    {
        var settings = new I2pSettings
        {
            Enabled = true,
            SamHostname = "127.0.0.1",
            SamPort = 7656
        };
        var monitor = new Mock<IOptionsMonitor<I2pSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(settings);
        return new I2pService(monitor.Object, System.IO.Path.GetTempPath());
    }
}
