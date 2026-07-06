using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.FileAllocator
{
    public enum AllocationError
    {
        InsufficientSpace,
        PermissionDenied,
        PathInvalid,
        DirectoryCreationFailed,
        FileCreationFailed,
        AllocationFailed
    }
}
