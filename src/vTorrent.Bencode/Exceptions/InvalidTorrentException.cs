using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Exceptions
{
    public class InvalidTorrentException : Exception
    {
        public InvalidTorrentException()
        {
        }

        public InvalidTorrentException(string message)
            : base(message)
        {
        }

        public InvalidTorrentException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public static InvalidTorrentException ForField(string fieldName, string reason)
        {
            return new InvalidTorrentException($"Invalid torrent field '{fieldName}': {reason}");
        }

        public static InvalidTorrentException MissingField(string fieldName)
        {
            return new InvalidTorrentException($"Required torrent field '{fieldName}' is missing");
        }
    }
}
