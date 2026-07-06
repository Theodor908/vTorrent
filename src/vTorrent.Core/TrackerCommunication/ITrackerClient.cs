using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Core.TrackerCommunication.Models;

namespace vTorrent.Core.TrackerCommunication
{
    public interface ITrackerClient : IDisposable
    {
        string TrackerUrl { get; }

        TrackerType Type { get; }

        bool IsAvailable { get; }

        DateTime? LastAnnounce { get; }

        int FailureCount { get; }

        Task<TrackerResponse> AnnounceAsync(TrackerRequest request, CancellationToken cancellationToken = default);

        Task<ScrapeResponse> ScrapeAsync(byte[] infoHash, CancellationToken cancellationToken = default);
    }
}
