using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.FileAllocator
{
    public enum AllocationStrategy
    {
        Sparse, // Sets the file size, doesn't write any data
        Full, // writes zero to the entire file, guarantees space
        None // Skip allocation
    }
}
