using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using vTorrent.Core.Network;
using vTorrent.Bencode.Parsers;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Network.I2P;
using vTorrent.Core.TrackerCommunication.Http;
using vTorrent.Core.TrackerCommunication.I2P;
using vTorrent.Core.TrackerCommunication.Udp;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.TrackerCommunication
{
    public class TrackerClientFactory
    {

        private readonly IOptionsMonitor<TrackerSettings> _trackerMonitor;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IBencodeParser _bencodeParser;
        private readonly IOptionsMonitor<PrivacySettings> _privacyMonitor;
        private readonly DnsCache _dnsCache;
        private readonly UdpSocketManager? _udpSocketManager;
        private readonly Udp.UdpTrackerPacketHandler? _trackerPacketHandler;
        private I2pService? _i2pService;

        public TrackerClientFactory(IOptionsMonitor<TrackerSettings> trackerMonitor, ILoggerFactory loggerFactory, IBencodeParser bencodeParser, IOptionsMonitor<PrivacySettings> privacyMonitor = null,
            UdpSocketManager? udpSocketManager = null,
            Udp.UdpTrackerPacketHandler? trackerPacketHandler = null)
        {
            _trackerMonitor = trackerMonitor ?? throw new ArgumentNullException(nameof(trackerMonitor));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _bencodeParser = bencodeParser ?? throw new ArgumentNullException(nameof(bencodeParser));
            _privacyMonitor = privacyMonitor;
            _dnsCache = new DnsCache(defaultTtl: TimeSpan.FromMinutes(5));
            _udpSocketManager = udpSocketManager;
            _trackerPacketHandler = trackerPacketHandler;
        }

        /// <summary>
        /// Sets the I2P service for lazy session resolution.
        /// The factory will pull the session from the service when needed.
        /// </summary>
        public void SetI2pService(I2pService? service)
        {
            _i2pService = service;
        }

        public ITrackerClient CreateClient(string trackerUrl)
        {
            if (string.IsNullOrWhiteSpace(trackerUrl))
                throw new ArgumentException("Tracker URL cannot be empty", nameof(trackerUrl));

            var protocol = GetProtocol(trackerUrl);

            return protocol switch
            {
                TrackerProtocol.Http => CreateHttpClient(trackerUrl),
                TrackerProtocol.Https => CreateHttpClient(trackerUrl),
                TrackerProtocol.Udp => CreateUdpClient(trackerUrl),
                TrackerProtocol.I2p => CreateI2pClient(trackerUrl),
                TrackerProtocol.Unknown => throw new NotSupportedException($"Unknown tracker protocol in URL: {trackerUrl}"),
                _ => throw new NotSupportedException($"Tracker protocol {protocol} is not yet supported")
            };
        }

        public static TrackerProtocol GetProtocol(string trackerUrl)
        {
            if (string.IsNullOrWhiteSpace(trackerUrl))
                return TrackerProtocol.Unknown;

            trackerUrl = trackerUrl.ToLowerInvariant();

            // I2P detection takes priority over scheme
            if (trackerUrl.Contains(".i2p/") || trackerUrl.EndsWith(".i2p"))
                return TrackerProtocol.I2p;

            if (trackerUrl.StartsWith("http://"))
                return TrackerProtocol.Http;

            if (trackerUrl.StartsWith("https://"))
                return TrackerProtocol.Https;

            if (trackerUrl.StartsWith("udp://"))
                return TrackerProtocol.Udp;

            return TrackerProtocol.Unknown;
        }

        public bool IsSupported(string trackerUrl)
        {
            var protocol = GetProtocol(trackerUrl);
            return protocol == TrackerProtocol.Http || protocol == TrackerProtocol.Https ||
                   protocol == TrackerProtocol.Udp || protocol == TrackerProtocol.I2p;
        }

        private ITrackerClient CreateHttpClient(string trackerUrl)
        {
            var logger = _loggerFactory.CreateLogger<HttpTrackerClient>();
            return new HttpTrackerClient(trackerUrl, _trackerMonitor, logger, _bencodeParser, privacyMonitor: _privacyMonitor);
        }

        private ITrackerClient CreateUdpClient(string trackerUrl)
        {
            var logger = _loggerFactory.CreateLogger<Udp.UdpTrackerClient>();
            return new UdpTrackerClient(trackerUrl, _trackerMonitor, logger, _dnsCache,
                _udpSocketManager, _trackerPacketHandler);
        }

        private ITrackerClient CreateI2pClient(string trackerUrl)
        {
            if (_i2pService == null)
                throw new InvalidOperationException("I2P service not configured");

            var logger = _loggerFactory.CreateLogger<I2pHttpTrackerClient>();
            return new I2pHttpTrackerClient(trackerUrl, _i2pService, logger);
        }

    }
}
