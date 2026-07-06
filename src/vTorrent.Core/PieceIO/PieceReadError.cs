using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PieceIO
{
    public enum PieceReadError
    {
        InvalidPieceIndex,
        FileNotFound,
        PermissionDenied,
        IoError,
        IncompleteData,
        HashMismatch,
        UnknownError
    }
}
