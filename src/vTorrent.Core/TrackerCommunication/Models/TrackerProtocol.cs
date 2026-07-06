using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.TrackerCommunication.Models
{
    public enum TrackerProtocol
    {
        Unknown,
        Http,
        Https,
        Udp,
        I2p
    }
}
