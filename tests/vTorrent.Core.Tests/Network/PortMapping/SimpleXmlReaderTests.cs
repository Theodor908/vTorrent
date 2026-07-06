using FluentAssertions;
using Xunit;
using vTorrent.Core.Network.PortMapping;

namespace vTorrent.Core.Tests.Network.PortMapping;

public class SimpleXmlReaderTests
{
    [Fact]
    public void Parse_StartTag_CallsDelegate()
    {
        var tags = new List<(SimpleXmlReader.XmlTokenType, string)>();
        SimpleXmlReader.Parse("<root>", (type, value) =>
            tags.Add((type, value.ToString())));

        tags.Should().Contain((SimpleXmlReader.XmlTokenType.StartTag, "root"));
    }

    [Fact]
    public void Parse_EndTag_CallsDelegate()
    {
        var tags = new List<(SimpleXmlReader.XmlTokenType, string)>();
        SimpleXmlReader.Parse("</root>", (type, value) =>
            tags.Add((type, value.ToString())));

        tags.Should().Contain((SimpleXmlReader.XmlTokenType.EndTag, "root"));
    }

    [Fact]
    public void Parse_TextContent_CallsDelegate()
    {
        var tags = new List<(SimpleXmlReader.XmlTokenType, string)>();
        SimpleXmlReader.Parse("<tag>hello</tag>", (type, value) =>
            tags.Add((type, value.ToString())));

        tags.Should().ContainInOrder(
            (SimpleXmlReader.XmlTokenType.StartTag, "tag"),
            (SimpleXmlReader.XmlTokenType.Text, "hello"),
            (SimpleXmlReader.XmlTokenType.EndTag, "tag"));
    }

    [Fact]
    public void Parse_StripsNamespacePrefix()
    {
        var tags = new List<(SimpleXmlReader.XmlTokenType, string)>();
        SimpleXmlReader.Parse("<u:AddPortMapping>", (type, value) =>
            tags.Add((type, value.ToString())));

        tags.Should().Contain((SimpleXmlReader.XmlTokenType.StartTag, "AddPortMapping"));
    }

    [Fact]
    public void Parse_TagWithAttributes_StripsAttributes()
    {
        var tags = new List<(SimpleXmlReader.XmlTokenType, string)>();
        SimpleXmlReader.Parse("<Envelope xmlns:s=\"http://example.com\">", (type, value) =>
            tags.Add((type, value.ToString())));

        tags.Should().Contain((SimpleXmlReader.XmlTokenType.StartTag, "Envelope"));
    }

    [Fact]
    public void Parse_SelfClosingTag_EmitsStartAndEnd()
    {
        var tags = new List<(SimpleXmlReader.XmlTokenType, string)>();
        SimpleXmlReader.Parse("<br/>", (type, value) =>
            tags.Add((type, value.ToString())));

        tags.Should().Contain((SimpleXmlReader.XmlTokenType.StartTag, "br"));
        tags.Should().Contain((SimpleXmlReader.XmlTokenType.EndTag, "br"));
    }

    [Fact]
    public void Parse_XmlDeclaration_Ignored()
    {
        var tags = new List<(SimpleXmlReader.XmlTokenType, string)>();
        SimpleXmlReader.Parse("<?xml version=\"1.0\"?><root/>", (type, value) =>
            tags.Add((type, value.ToString())));

        tags.Should().NotContain(t => t.Item2.Contains("xml version"));
        tags.Should().Contain((SimpleXmlReader.XmlTokenType.StartTag, "root"));
    }

    [Fact]
    public void Parse_EmptyString_NoCallbacks()
    {
        var count = 0;
        SimpleXmlReader.Parse("", (_, _) => count++);
        count.Should().Be(0);
    }

    [Fact]
    public void Parse_MalformedXml_DoesNotThrow()
    {
        var act = () => SimpleXmlReader.Parse("<unclosed><nested>text",
            (_, _) => { });
        act.Should().NotThrow();
    }

    [Fact]
    public void Parse_NamespacedEndTag_StripsPrefix()
    {
        var tags = new List<(SimpleXmlReader.XmlTokenType, string)>();
        SimpleXmlReader.Parse("</s:Body>", (type, value) =>
            tags.Add((type, value.ToString())));

        tags.Should().Contain((SimpleXmlReader.XmlTokenType.EndTag, "Body"));
    }

    [Fact]
    public void Parse_RealUpnpDeviceXml_ExtractsServiceType()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <modelName>TestRouter</modelName>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                    <controlURL>/ctl/IPConn</controlURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;

        var foundServiceType = "";
        var foundControlUrl = "";
        var lastStartTag = "";

        SimpleXmlReader.Parse(xml, (type, value) =>
        {
            var s = value.ToString();
            if (type == SimpleXmlReader.XmlTokenType.StartTag)
                lastStartTag = s;
            else if (type == SimpleXmlReader.XmlTokenType.Text)
            {
                if (lastStartTag == "serviceType") foundServiceType = s;
                if (lastStartTag == "controlURL") foundControlUrl = s;
            }
        });

        foundServiceType.Should().Be("urn:schemas-upnp-org:service:WANIPConnection:1");
        foundControlUrl.Should().Be("/ctl/IPConn");
    }
}

public class UpnpXmlParsersTests
{
    [Fact]
    public void FindControlUrl_WanIpConnection_ExtractsAll()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <modelName>TestRouter 2000</modelName>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                    <controlURL>/ctl/IPConn</controlURL>
                  </service>
                </serviceList>
              </device>
              <URLBase>http://192.168.1.1:5000</URLBase>
            </root>
            """;

        var result = UpnpXmlParsers.FindControlUrl(xml);

        result.ServiceType.Should().Be("urn:schemas-upnp-org:service:WANIPConnection:1");
        result.ControlUrl.Should().Be("/ctl/IPConn");
        result.UrlBase.Should().Be("http://192.168.1.1:5000");
        result.Model.Should().Be("TestRouter 2000");
    }

    [Fact]
    public void FindControlUrl_WanPppConnection_Works()
    {
        const string xml = """
            <root>
              <device><serviceList><service>
                <serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType>
                <controlURL>/ppp</controlURL>
              </service></serviceList></device>
            </root>
            """;

        var result = UpnpXmlParsers.FindControlUrl(xml);
        result.ServiceType.Should().Be("urn:schemas-upnp-org:service:WANPPPConnection:1");
        result.ControlUrl.Should().Be("/ppp");
    }

    [Fact]
    public void FindControlUrl_NoService_ReturnsEmpty()
    {
        const string xml = "<root><device><modelName>Printer</modelName></device></root>";
        var result = UpnpXmlParsers.FindControlUrl(xml);
        result.ControlUrl.Should().BeEmpty();
        result.ServiceType.Should().BeEmpty();
    }

    [Fact]
    public void FindErrorCode_SoapFault_ExtractsCode()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><s:Fault><detail>
                <UPnPError><errorCode>718</errorCode></UPnPError>
              </detail></s:Fault></s:Body>
            </s:Envelope>
            """;

        UpnpXmlParsers.FindErrorCode(xml).Should().Be(718);
    }

    [Fact]
    public void FindErrorCode_NoError_ReturnsMinusOne()
    {
        const string xml = "<s:Envelope><s:Body><u:AddPortMappingResponse/></s:Body></s:Envelope>";
        UpnpXmlParsers.FindErrorCode(xml).Should().Be(-1);
    }

    [Fact]
    public void FindIpAddress_ExtractsIp()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><u:GetExternalIPAddressResponse>
                <NewExternalIPAddress>203.0.113.5</NewExternalIPAddress>
              </u:GetExternalIPAddressResponse></s:Body>
            </s:Envelope>
            """;

        UpnpXmlParsers.FindIpAddress(xml).Should().Be("203.0.113.5");
    }

    [Fact]
    public void FindIpAddress_NoIp_ReturnsNull()
    {
        const string xml = "<s:Envelope><s:Body><u:Response/></s:Body></s:Envelope>";
        UpnpXmlParsers.FindIpAddress(xml).Should().BeNull();
    }
}
