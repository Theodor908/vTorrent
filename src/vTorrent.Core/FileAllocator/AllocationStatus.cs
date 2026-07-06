using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.FileAllocator
{
    public class AllocationStatus
    {
        public bool AllFilesExist { get; set; }
        public bool AllSizesCorrect { get; set; }
        public List<string> MissingFiles { get; set; }
        public List<string> IncorrectSizeFiles { get; set; }
        public bool IsComplete => AllFilesExist && AllSizesCorrect;

        public AllocationStatus()
        {
            MissingFiles = new List<string>();
            IncorrectSizeFiles = new List<string>();
        }
    }
}
