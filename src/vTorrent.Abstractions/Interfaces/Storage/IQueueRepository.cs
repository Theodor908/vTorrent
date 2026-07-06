using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Records;

namespace vTorrent.Abstractions.Interfaces.Storage;

public interface IQueueRepository
{
    Task<int> GetNextQueuePositionAsync();
    Task UpdateQueuePositionAsync(string infoHash, int position);
    Task ReorderQueueAfterRemovalAsync(int removedPosition);
    Task BatchUpdateQueuePositionsAsync(IEnumerable<QueuePositionUpdate> updates);
}
