using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace vTorrent.Core.PieceIO
{
    public class PieceLocation
    {
        public int PieceIndex { get; set; }
        public long PieceSize { get; set; }
        // List of file segments that make up this piece
        public List<FileSegment> FileSegments { get; set; }

        public PieceLocation()
        {
            FileSegments = new List<FileSegment>();
        }
    }
}
