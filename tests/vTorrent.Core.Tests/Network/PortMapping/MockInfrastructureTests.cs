using FluentAssertions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace vTorrent.Core.Tests.Network.PortMapping;

public class MockInfrastructureTests
{
    [Fact]
    public async Task MockSsdpServer_RespondsToMSearch()
    {
        await using var ssdp = new MockSsdpServer();
        ssdp.LocationUrl = "http://192.168.1.1:5000/desc.xml";
        ssdp.Start();

        using var udp = new UdpClient();
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nST:upnp:rootdevice\r\n\r\n");
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Loopback, ssdp.Port));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await udp.ReceiveAsync(cts.Token);
        var response = Encoding.ASCII.GetString(result.Buffer);

        response.Should().Contain("Location: http://192.168.1.1:5000/desc.xml");
        ssdp.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task MockUpnpDevice_ServesDescriptionXml()
    {
        await using var device = new MockUpnpDevice();
        device.Start();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetStringAsync($"{device.BaseUrl}/desc.xml");

        response.Should().Contain("WANIPConnection:1");
        response.Should().Contain("/ctl/IPConn");
        response.Should().Contain("MockRouter");
    }

    [Fact]
    public async Task MockUpnpDevice_HandlesSoapAddMapping()
    {
        await using var device = new MockUpnpDevice();
        device.Start();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var content = new StringContent("<s:Envelope/>", Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"{device.ServiceType}#AddPortMapping\"");
        var response = await http.PostAsync($"{device.BaseUrl}{device.ControlPath}", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("AddPortMappingResponse");
        device.SoapRequestCount.Should().Be(1);
    }
}
