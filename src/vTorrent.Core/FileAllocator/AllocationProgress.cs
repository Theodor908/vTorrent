using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.FileAllocator
{
    public class AllocationProgress
    {
        public int CurrentFileIndex { get; set; }
        public int TotalFiles { get; set; }
        public long BytesAllocated { get; set; }
        public long TotalBytes { get; set; }
        public string CurrentFileName { get; set; }
        public float PercentComplete => BytesAllocated == 0 ? 0 : (float)BytesAllocated / TotalBytes * 100f;
    }
}
