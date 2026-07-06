using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.TrackerCommunication.Models
{
    public enum TrackerEvent
    {
        None = 0,
        Started = 1,
        Stopped = 2,
        Completed = 3
    }
}
