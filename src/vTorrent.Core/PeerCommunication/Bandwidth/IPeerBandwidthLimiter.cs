using System;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.PeerCommunication.Bandwidth;

/// <summary>
/// Interface for bandwidth limiting at the peer connection level.
/// Based on libtorrent's bandwidth management pattern.
/// </summary>
public interface IPeerBandwidthLimiter
{
    /// <summary>
    /// Request download quota before receiving data.
    /// Returns the number of bytes that can be received immediately.
    /// If 0, the consumer should wait for OnQuotaAvailable callback.
    /// </summary>
    /// <param name="consumer">The peer connection requesting quota</param>
    /// <param name="bytes">Number of bytes requested</param>
    /// <returns>Bytes granted immediately (0 if queued)</returns>
    int RequestDownloadQuota(IPeerBandwidthConsumer consumer, int bytes);

    /// <summary>
    /// Request upload quota before sending data.
    /// Returns the number of bytes that can be sent immediately.
    /// If 0, the consumer should wait for OnQuotaAvailable callback.
    /// </summary>
    /// <param name="consumer">The peer connection requesting quota</param>
    /// <param name="bytes">Number of bytes requested</param>
    /// <returns>Bytes granted immediately (0 if queued)</returns>
    int RequestUploadQuota(IPeerBandwidthConsumer consumer, int bytes);

    /// <summary>
    /// Cancel all pending quota requests for a consumer.
    /// Called when peer disconnects.
    /// </summary>
    void CancelRequests(IPeerBandwidthConsumer consumer);

    /// <summary>
    /// Whether download bandwidth is limited.
    /// </summary>
    bool IsDownloadLimited { get; }

    /// <summary>
    /// Whether upload bandwidth is limited.
    /// </summary>
    bool IsUploadLimited { get; }

    /// <summary>
    /// Effective download limit in bytes/sec (0 = unlimited).
    /// </summary>
    int EffectiveDownloadLimit { get; }

    /// <summary>
    /// Effective upload limit in bytes/sec (0 = unlimited).
    /// </summary>
    int EffectiveUploadLimit { get; }
}

/// <summary>
/// Interface for bandwidth consumers (peer connections).
/// </summary>
public interface IPeerBandwidthConsumer
{
    /// <summary>
    /// Unique identifier for this consumer.
    /// </summary>
    string ConsumerId { get; }

    /// <summary>
    /// Whether this consumer is disconnecting.
    /// </summary>
    bool IsDisconnecting { get; }

    /// <summary>
    /// Priority for bandwidth allocation (1-255, default 128).
    /// Higher priority gets more bandwidth.
    /// </summary>
    int BandwidthPriority { get; }

    /// <summary>
    /// Called when download quota becomes available.
    /// </summary>
    /// <param name="bytes">Bytes of quota assigned</param>
    void OnDownloadQuotaAssigned(int bytes);

    /// <summary>
    /// Called when upload quota becomes available.
    /// </summary>
    /// <param name="bytes">Bytes of quota assigned</param>
    void OnUploadQuotaAssigned(int bytes);
}

