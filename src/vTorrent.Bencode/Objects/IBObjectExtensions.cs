using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Objects
{
    public static class IBObjectExtensions
    {

        public static byte[] EncodeAsBytes(this IBObject obj)
        {
            var size = obj.GetSizeInBytes();
            var buffer = new byte[size];
            obj.EncodeTo(buffer);
            return buffer;
        }

        public static string EncodeAsString(this IBObject obj, Encoding encoding = null)
        {
            encoding ??= Encoding.UTF8;
            var bytes = obj.EncodeAsBytes();
            return encoding.GetString(bytes);
        }

        public static void EncodeTo(this IBObject obj, string filePath)
        {
            using var stream = File.Create(filePath);
            obj.EncodeTo(stream);
        }

        public static async ValueTask EncodeToAsync(this IBObject obj, string filePath, CancellationToken cancellationToken = default)
        {
            using var stream = File.Create(filePath);
            await obj.EncodeToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

    }
}
