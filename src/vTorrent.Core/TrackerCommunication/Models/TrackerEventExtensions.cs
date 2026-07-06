using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.TrackerCommunication.Models
{
    public static class TrackerEventExtensions
    {
        public static string ToQueryString(this TrackerEvent trackerEvent)
        {
            return trackerEvent switch
            {
                TrackerEvent.None => string.Empty,
                TrackerEvent.Started => "started",
                TrackerEvent.Stopped => "stopped",
                TrackerEvent.Completed => "completed",
                _ => string.Empty
            };
        }

        public static int ToUdpValue(this TrackerEvent trackerEvent)
        {
            return trackerEvent switch
            {
                TrackerEvent.None => 0,
                TrackerEvent.Completed => 1,
                TrackerEvent.Started => 2,
                TrackerEvent.Stopped => 3,
                _ => 0
            };
        }
    }
}
