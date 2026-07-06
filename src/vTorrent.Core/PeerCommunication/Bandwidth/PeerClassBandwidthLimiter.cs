using System;
using vTorrent.Core.Orchestration.Bandwidth;

namespace vTorrent.Core.PeerCommunication.Bandwidth;

/// <summary>
/// Composites an existing torrent-level bandwidth limiter with peer-class bandwidth channels.
/// The class channels act as additional rate-limiting gates: a request must pass both the
/// torrent-level limiter AND the class channel. This follows libtorrent's multi-channel
/// bandwidth model where each peer can belong to multiple bandwidth channels.
/// </summary>
internal sealed class PeerClassBandwidthLimiter : IPeerBandwidthLimiter
{
    private readonly IPeerBandwidthLimiter? _inner;
    private readonly BandwidthChannel _uploadChannel;
    private readonly BandwidthChannel _downloadChannel;

    public PeerClassBandwidthLimiter(
        IPeerBandwidthLimiter? inner,
        BandwidthChannel uploadChannel,
        BandwidthChannel downloadChannel)
    {
        _inner = inner;
        _uploadChannel = uploadChannel ?? throw new ArgumentNullException(nameof(uploadChannel));
        _downloadChannel = downloadChannel ?? throw new ArgumentNullException(nameof(downloadChannel));
    }

    public bool IsDownloadLimited => !_downloadChannel.IsUnlimited || (_inner?.IsDownloadLimited ?? false);

    public bool IsUploadLimited => !_uploadChannel.IsUnlimited || (_inner?.IsUploadLimited ?? false);

    public int EffectiveDownloadLimit
    {
        get
        {
            int classLimit = _downloadChannel.Throttle;
            int innerLimit = _inner?.EffectiveDownloadLimit ?? 0;
            if (classLimit == 0) return innerLimit;
            if (innerLimit == 0) return classLimit;
            return Math.Min(classLimit, innerLimit);
        }
    }

    public int EffectiveUploadLimit
    {
        get
        {
            int classLimit = _uploadChannel.Throttle;
            int innerLimit = _inner?.EffectiveUploadLimit ?? 0;
            if (classLimit == 0) return innerLimit;
            if (innerLimit == 0) return classLimit;
            return Math.Min(classLimit, innerLimit);
        }
    }

    public int RequestDownloadQuota(IPeerBandwidthConsumer consumer, int bytes)
    {
        if (consumer == null || bytes <= 0) return 0;

        // Check class channel first
        if (!_downloadChannel.IsUnlimited)
        {
            if (_downloadChannel.NeedsQueueing(bytes))
                return 0; // Class channel exhausted

            // Tentatively check — don't consume yet until inner also approves
        }

        // Check inner limiter
        int granted = _inner != null
            ? _inner.RequestDownloadQuota(consumer, bytes)
            : bytes;

        if (granted <= 0)
            return 0;

        // Both approved — consume from class channel
        if (!_downloadChannel.IsUnlimited)
            _downloadChannel.UseQuota(granted);

        return granted;
    }

    public int RequestUploadQuota(IPeerBandwidthConsumer consumer, int bytes)
    {
        if (consumer == null || bytes <= 0) return 0;

        // Check class channel first
        if (!_uploadChannel.IsUnlimited)
        {
            if (_uploadChannel.NeedsQueueing(bytes))
                return 0; // Class channel exhausted
        }

        // Check inner limiter
        int granted = _inner != null
            ? _inner.RequestUploadQuota(consumer, bytes)
            : bytes;

        if (granted <= 0)
            return 0;

        // Both approved — consume from class channel
        if (!_uploadChannel.IsUnlimited)
            _uploadChannel.UseQuota(granted);

        return granted;
    }

    public void CancelRequests(IPeerBandwidthConsumer consumer)
    {
        _inner?.CancelRequests(consumer);
    }
}
