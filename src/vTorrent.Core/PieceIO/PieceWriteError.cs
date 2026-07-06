using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PieceIO
{
    public enum PieceWriteError
    {
        InvalidPieceIndex,
        InvalidData,
        InvalidDataSize,
        FileNotFound,
        PermissionDenied,
        IoError,
        IncompleteData,
        HashMismatch,
        UnknownError
    }
}
