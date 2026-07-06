using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.DHT
{
    /// <summary>
    /// Manages DHT RPC transactions - matching requests with responses,
    /// handling timeouts, and tracking pending queries.
    /// </summary>
    public class RpcManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<DhtSettings> _dhtMonitor;
        private readonly ConcurrentDictionary<ushort, PendingQuery> _pendingQueries;
        private readonly Timer _timeoutTimer;

        private int _nextTransactionId;
        private bool _disposed;

        /// <summary>
        /// Event raised when a query times out.
        /// </summary>
        public event Action<PendingQuery> QueryTimedOut;

        /// <summary>
        /// Number of pending queries.
        /// </summary>
        public int PendingCount => _pendingQueries.Count;

        public RpcManager(IOptionsMonitor<DhtSettings> dhtMonitor, ILogger logger = null)
        {
            _dhtMonitor = dhtMonitor ?? throw new ArgumentNullException(nameof(dhtMonitor));
            _logger = logger;
            _pendingQueries = new ConcurrentDictionary<ushort, PendingQuery>();

            // Start with a random transaction ID
            Span<byte> randomBytes = stackalloc byte[2];
            RandomNumberGenerator.Fill(randomBytes);
            _nextTransactionId = BinaryPrimitives.ReadUInt16LittleEndian(randomBytes);

            // Timer to check for timeouts
            _timeoutTimer = new Timer(CheckTimeouts, null, 1000, 1000);
        }

        /// <summary>
        /// Generates a new unique transaction ID.
        /// </summary>
        public byte[] GenerateTransactionId()
        {
            ushort id = (ushort)Interlocked.Increment(ref _nextTransactionId);
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, id);
            return bytes;
        }

        /// <summary>
        /// Gets the numeric value of a transaction ID.
        /// </summary>
        public static ushort GetTransactionIdValue(byte[] transactionId)
        {
            if (transactionId == null || transactionId.Length < 2)
                return 0;
            return BinaryPrimitives.ReadUInt16BigEndian(transactionId);
        }

        /// <summary>
        /// Registers a pending query and returns a task that completes when the response arrives.
        /// </summary>
        public Task<DhtMessage> RegisterQueryAsync(DhtMessage query, IPEndPoint target,
            CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (target == null) throw new ArgumentNullException(nameof(target));

            var tcs = new TaskCompletionSource<DhtMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            var pending = new PendingQuery
            {
                TransactionId = GetTransactionIdValue(query.TransactionId),
                Query = query,
                Target = target,
                SentTime = DateTime.UtcNow,
                Completion = tcs
            };

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    if (_pendingQueries.TryRemove(pending.TransactionId, out _))
                    {
                        tcs.TrySetCanceled(cancellationToken);
                    }
                });
            }

            if (!_pendingQueries.TryAdd(pending.TransactionId, pending))
            {
                // Transaction ID collision, very rare
                _logger?.LogWarning("Transaction ID collision: {TransactionId}", pending.TransactionId);
                tcs.TrySetException(new InvalidOperationException("Transaction ID collision"));
            }

            return tcs.Task;
        }

        /// <summary>
        /// Handles an incoming response message, matching it to a pending query.
        /// Returns true if the response was matched to a pending query.
        /// </summary>
        public bool HandleResponse(DhtMessage response)
        {
            if (response == null) return false;
            if (response.MessageType != DhtMessageType.Response &&
                response.MessageType != DhtMessageType.Error)
                return false;

            ushort tid = GetTransactionIdValue(response.TransactionId);

            if (_pendingQueries.TryRemove(tid, out var pending))
            {
                var rtt = (int)(DateTime.UtcNow - pending.SentTime).TotalMilliseconds;
                response.SourceEndpoint = pending.Target;

                _logger?.LogDebug("Received response for transaction {TransactionId} from {Target} (RTT: {Rtt}ms)",
                    tid, pending.Target, rtt);

                if (response.MessageType == DhtMessageType.Error)
                {
                    pending.Completion.TrySetException(
                        new DhtException($"DHT error {response.ErrorCode}: {response.ErrorMessage}"));
                }
                else
                {
                    pending.Completion.TrySetResult(response);
                }

                pending.RttMs = rtt;
                return true;
            }

            _logger?.LogDebug("Received response for unknown transaction {TransactionId}", tid);
            return false;
        }

        /// <summary>
        /// Checks for timed out queries and notifies.
        /// </summary>
        private void CheckTimeouts(object state)
        {
            if (_disposed) return;

            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromMilliseconds(_dhtMonitor.CurrentValue.QueryTimeoutMs);

            foreach (var kvp in _pendingQueries)
            {
                var pending = kvp.Value;
                if (now - pending.SentTime > timeout)
                {
                    if (_pendingQueries.TryRemove(kvp.Key, out _))
                    {
                        _logger?.LogDebug("Query timed out: {TransactionId} to {Target}",
                            pending.TransactionId, pending.Target);

                        pending.Completion.TrySetException(
                            new TimeoutException($"DHT query to {pending.Target} timed out"));

                        QueryTimedOut?.Invoke(pending);
                    }
                }
            }
        }

        /// <summary>
        /// Cancels all pending queries.
        /// </summary>
        public void CancelAll()
        {
            foreach (var kvp in _pendingQueries)
            {
                if (_pendingQueries.TryRemove(kvp.Key, out var pending))
                {
                    pending.Completion.TrySetCanceled();
                }
            }
        }

        /// <summary>
        /// Gets the pending query for a transaction ID if it exists.
        /// </summary>
        public PendingQuery GetPendingQuery(ushort transactionId)
        {
            _pendingQueries.TryGetValue(transactionId, out var pending);
            return pending;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timeoutTimer?.Dispose();
            CancelAll();
        }
    }

    /// <summary>
    /// Represents a pending DHT query awaiting response.
    /// </summary>
    public class PendingQuery
    {
        /// <summary>
        /// The transaction ID value.
        /// </summary>
        public ushort TransactionId { get; set; }

        /// <summary>
        /// The query message that was sent.
        /// </summary>
        public DhtMessage Query { get; set; }

        /// <summary>
        /// The target endpoint the query was sent to.
        /// </summary>
        public IPEndPoint Target { get; set; }

        /// <summary>
        /// When the query was sent.
        /// </summary>
        public DateTime SentTime { get; set; }

        /// <summary>
        /// The completion source for the response.
        /// </summary>
        public TaskCompletionSource<DhtMessage> Completion { get; set; }

        /// <summary>
        /// The measured RTT in milliseconds (set when response received).
        /// </summary>
        public int RttMs { get; set; }
    }

    /// <summary>
    /// Exception thrown for DHT-specific errors.
    /// </summary>
    public class DhtException : Exception
    {
        public DhtException(string message) : base(message) { }
        public DhtException(string message, Exception innerException) : base(message, innerException) { }
    }
}
