using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PieceIO
{
    public class PieceReadResult
    {
        public bool IsSuccess { get; set; }
        public PieceReadError? ErrorType { get; set; }
        public int PieceIndex { get; set; }
        public byte[] Data { get; set; }
        public long BytesRead {get; set;}
        public string ErrorMessage {get; set;}
        public bool HashVerified {get; set;}

        public static PieceReadResult Success(int pieceIndex, byte[] data, bool hashVerified)
        {
            return new PieceReadResult
            {
                IsSuccess = true,
                PieceIndex = pieceIndex,
                Data = data,
                BytesRead = data?.Length ?? 0,
                HashVerified = hashVerified
            };
        }

        public static PieceReadResult Failure(int pieceIndex, PieceReadError errorType, string errorMessage)
        {
            return new PieceReadResult
            {
                IsSuccess = false,
                PieceIndex = pieceIndex,
                ErrorType = errorType,
                ErrorMessage = errorMessage,
                Data = null,
                BytesRead = 0,
                HashVerified = false
            };
        }
    }
}
