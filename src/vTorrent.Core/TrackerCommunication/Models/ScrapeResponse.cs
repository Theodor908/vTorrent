using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Core.TrackerCommunication.Models
{
    public class ScrapeResponse
    {

        public int Complete { get; set; }

        public int Incomplete { get; set; }

        public int Downloaded { get; set; }

        public string Name { get; set; }

        public bool IsSuccess { get; set; }

        public string ErrorMessage { get; set; }

        public DateTime ScrapedAt { get; set; }

        public ScrapeResponse()
        {
            ScrapedAt = DateTime.UtcNow;
        }

        public static ScrapeResponse CreateSuccess(int complete, int incomplete, int downloaded, string name = null)
        {
            return new ScrapeResponse
            {
                Complete = complete,
                Incomplete = incomplete,
                Downloaded = downloaded,
                Name = name,
                IsSuccess = true
            };
        }

        public static ScrapeResponse CreateFailure(string errorMessage)
        {
            return new ScrapeResponse
            {
                IsSuccess = false,
                ErrorMessage = errorMessage
            };
        }

        public override string ToString()
        {
            if (!IsSuccess)
                return $"ScrapeResponse [Failed: {ErrorMessage}]";

            return $"ScrapeResponse [Seeders: {Complete}, Leechers: {Incomplete}, " +
                   $"Downloaded: {Downloaded}]";
        }
    }
}
