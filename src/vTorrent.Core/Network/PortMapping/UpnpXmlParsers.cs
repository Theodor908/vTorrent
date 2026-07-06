using System;

namespace vTorrent.Core.Network.PortMapping;

/// <summary>
/// UPnP XML response parsers built on SimpleXmlReader.
/// </summary>
internal static class UpnpXmlParsers
{
    public readonly struct DeviceDescription
    {
        public string ServiceType { get; init; }
        public string ControlUrl { get; init; }
        public string UrlBase { get; init; }
        public string Model { get; init; }
    }

    public static DeviceDescription FindControlUrl(string xml)
    {
        string serviceType = "", controlUrl = "", urlBase = "", model = "";
        bool inService = false;
        bool foundService = false;
        string lastTag = "";

        SimpleXmlReader.Parse(xml, (type, value) =>
        {
            var s = value.ToString();
            if (type == SimpleXmlReader.XmlTokenType.StartTag)
            {
                lastTag = s;
            }
            else if (type == SimpleXmlReader.XmlTokenType.EndTag)
            {
                if (inService && s.Equals("service", StringComparison.OrdinalIgnoreCase))
                    inService = false;
            }
            else if (type == SimpleXmlReader.XmlTokenType.Text)
            {
                if (!foundService
                    && lastTag.Equals("serviceType", StringComparison.OrdinalIgnoreCase)
                    && IsWanService(s))
                {
                    serviceType = s;
                    inService = true;
                    foundService = true;
                }
                else if (inService && controlUrl.Length == 0
                    && lastTag.Equals("controlURL", StringComparison.OrdinalIgnoreCase))
                {
                    controlUrl = s;
                }
                else if (model.Length == 0
                    && lastTag.Equals("modelName", StringComparison.OrdinalIgnoreCase))
                {
                    model = s;
                }
                else if (lastTag.Equals("URLBase", StringComparison.OrdinalIgnoreCase))
                {
                    urlBase = s;
                }
            }
        });

        return new DeviceDescription
        {
            ServiceType = serviceType,
            ControlUrl = controlUrl,
            UrlBase = urlBase,
            Model = model
        };
    }

    public static int FindErrorCode(string xml)
    {
        int errorCode = -1;
        bool inErrorCode = false;

        SimpleXmlReader.Parse(xml, (type, value) =>
        {
            if (errorCode != -1) return;
            var s = value.ToString();
            if (type == SimpleXmlReader.XmlTokenType.StartTag
                && s.Equals("errorCode", StringComparison.OrdinalIgnoreCase))
            {
                inErrorCode = true;
            }
            else if (type == SimpleXmlReader.XmlTokenType.Text && inErrorCode)
            {
                int.TryParse(s, out errorCode);
            }
        });

        return errorCode;
    }

    public static string? FindIpAddress(string xml)
    {
        string? ip = null;
        bool inIp = false;

        SimpleXmlReader.Parse(xml, (type, value) =>
        {
            if (ip != null) return;
            var s = value.ToString();
            if (type == SimpleXmlReader.XmlTokenType.StartTag
                && s.Equals("NewExternalIPAddress", StringComparison.OrdinalIgnoreCase))
            {
                inIp = true;
            }
            else if (type == SimpleXmlReader.XmlTokenType.Text && inIp)
            {
                ip = s;
            }
        });

        return ip;
    }

    private static bool IsWanService(string serviceType)
    {
        return serviceType.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase)
            || serviceType.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase);
    }
}
