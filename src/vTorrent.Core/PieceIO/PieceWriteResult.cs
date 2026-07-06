using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PieceIO
{
    public class PieceWriteResult
    {
        public bool IsSuccess { get; set; }
        public PieceWriteError? ErrorType { get; set; }
        public int PieceIndex { get; set; }
        public long BytesWritten { get; set; }
        public string ErrorMessage { get; set; }
        public bool HashVerified { get; set; }

        public static PieceWriteResult Success(int pieceIndex, long bytesWritten, bool hashVerified)
        {
            return new PieceWriteResult
            {
                IsSuccess = true,
                PieceIndex = pieceIndex,
                BytesWritten = bytesWritten,
                HashVerified = hashVerified
            };
        }

        public static PieceWriteResult Failure(int pieceIndex, PieceWriteError errorType, string errorMessage)
        {
            return new PieceWriteResult
            {
                IsSuccess = false,
                PieceIndex = pieceIndex,
                ErrorType = errorType,
                ErrorMessage = errorMessage,
                BytesWritten = 0,
                HashVerified = false
            };
        }
    }
}
