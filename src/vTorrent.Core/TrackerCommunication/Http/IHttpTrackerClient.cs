using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.TrackerCommunication.Http
{
    internal interface IHttpTrackerClient : ITrackerClient
    {
        bool FollowRedirects { get; set; }

        int MaxRedirects { get; set; }

        string UserAgent { get; set; }

        IDictionary<string, string> LastResponseHeaders { get; }
    }
}
