using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.Tests.Network.PortMapping;

/// <summary>
/// Mock UPnP device HTTP server for testing.
/// Serves device description XML and handles SOAP requests.
/// </summary>
public sealed class MockUpnpDevice : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private int _soapRequestCount;

    public int Port { get; }
    public string BaseUrl { get; }
    public int SoapRequestCount => _soapRequestCount;

    public string ServiceType { get; set; } = "urn:schemas-upnp-org:service:WANIPConnection:1";
    public string ControlPath { get; set; } = "/ctl/IPConn";
    public string? ModelName { get; set; } = "MockRouter";
    public string ExternalIp { get; set; } = "203.0.113.1";
    public int SoapErrorCode { get; set; } = -1;
    public bool SilentSoap { get; set; } = false;

    public MockUpnpDevice()
    {
        using var temp = new TcpListener(IPAddress.Loopback, 0);
        temp.Start();
        Port = ((IPEndPoint)temp.LocalEndpoint).Port;
        temp.Stop();

        BaseUrl = $"http://localhost:{Port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{BaseUrl}/");
    }

    public void Start()
    {
        _listener.Start();
        _listenTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                    _ = Task.Run(() => HandleRequest(ctx));
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch { }
            }
        });
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod == "GET")
            {
                var xml = BuildDeviceDescriptionXml();
                var bytes = Encoding.UTF8.GetBytes(xml);
                ctx.Response.ContentType = "text/xml";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes);
            }
            else if (ctx.Request.HttpMethod == "POST")
            {
                Interlocked.Increment(ref _soapRequestCount);

                if (SilentSoap)
                {
                    ctx.Response.Abort();
                    return;
                }

                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = reader.ReadToEnd();
                var soapAction = ctx.Request.Headers["SOAPAction"] ?? "";

                string responseXml;
                if (SoapErrorCode != -1)
                {
                    responseXml = BuildSoapFault(SoapErrorCode);
                    ctx.Response.StatusCode = 500;
                }
                else if (soapAction.Contains("GetExternalIPAddress"))
                {
                    responseXml = BuildGetExternalIpResponse();
                }
                else if (soapAction.Contains("AddPortMapping"))
                {
                    responseXml = BuildAddPortMappingResponse();
                }
                else if (soapAction.Contains("DeletePortMapping"))
                {
                    responseXml = BuildDeletePortMappingResponse();
                }
                else
                {
                    responseXml = BuildSoapFault(401);
                    ctx.Response.StatusCode = 500;
                }

                var respBytes = Encoding.UTF8.GetBytes(responseXml);
                ctx.Response.ContentType = "text/xml";
                ctx.Response.ContentLength64 = respBytes.Length;
                ctx.Response.OutputStream.Write(respBytes);
            }
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    private string BuildDeviceDescriptionXml() =>
        $"""
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <modelName>{ModelName}</modelName>
            <serviceList>
              <service>
                <serviceType>{ServiceType}</serviceType>
                <controlURL>{ControlPath}</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;

    private string BuildGetExternalIpResponse() =>
        $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <u:GetExternalIPAddressResponse xmlns:u="{ServiceType}">
              <NewExternalIPAddress>{ExternalIp}</NewExternalIPAddress>
            </u:GetExternalIPAddressResponse>
          </s:Body>
        </s:Envelope>
        """;

    private string BuildAddPortMappingResponse() =>
        """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <u:AddPortMappingResponse/>
          </s:Body>
        </s:Envelope>
        """;

    private string BuildDeletePortMappingResponse() =>
        """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <u:DeletePortMappingResponse/>
          </s:Body>
        </s:Envelope>
        """;

    private static string BuildSoapFault(int errorCode) =>
        $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <s:Fault>
              <faultcode>s:Client</faultcode>
              <faultstring>UPnPError</faultstring>
              <detail>
                <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>{errorCode}</errorCode>
                </UPnPError>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_listenTask != null)
            try { await _listenTask; } catch { }
        _listener.Close();
        _cts.Dispose();
    }
}
