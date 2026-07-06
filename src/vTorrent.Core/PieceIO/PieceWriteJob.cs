using System.Threading.Tasks;

namespace vTorrent.Core.PieceIO;

public class PieceWriteJob
{
    public int PieceIndex { get; init; }
    public byte[] Data { get; init; }
    public TaskCompletionSource<PieceWriteResult> Completion { get; init; }
}
