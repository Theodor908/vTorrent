using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.PieceIO
{
    public class FileSegment
    {
        // Absolute path to the file
        public string FilePath { get; set; }
        // Byte offset within the file where this segment starts
        public long FileOffset { get; set; }
        // Byte offset within the piece where this segment's data starts
        public long PieceOffset { get; set; }
        // Number of bytes to read/write in this file
        public long Length { get; set; }
        // Index of the file in the torrent's file list (-1 for single-file torrents)
        public int FileIndex { get; set; } = -1;
    }
}