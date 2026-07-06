using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Bencode.Objects
{
    public static class BencodeExtensions
    {

        public static string EncodeAsString(this IBObject obj, Encoding encoding = null)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            encoding ??= Encoding.UTF8;
            var bytes = obj.EncodeAsBytes();
            return encoding.GetString(bytes);
        }

        public static void EncodeToFile(this IBObject obj, string filePath)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            using var stream = File.Create(filePath);
            obj.EncodeTo(stream);
        }

        public static async ValueTask EncodeToFileAsync(this IBObject obj, string filePath, CancellationToken cancellationToken = default)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            using var stream = File.Create(filePath);
            await obj.EncodeToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        public static int EncodeTo(this IBObject obj, IBufferWriter<byte> bufferWriter)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            if (bufferWriter == null) throw new ArgumentNullException(nameof(bufferWriter));

            var size = obj.GetSizeInBytes();
            var span = bufferWriter.GetSpan(size);
            var bytesWritten = obj.EncodeTo(span);
            bufferWriter.Advance(bytesWritten);
            return bytesWritten;
        }

        public static string ToDebugString(this IBObject obj, int indent = 0)
        {
            if (obj == null) return "null";

            var indentStr = new string(' ', indent * 2);

            return obj switch
            {
                BString bstr => $"{indentStr}\"{bstr}\"",
                BNumber bnum => $"{indentStr}{bnum.Value}",
                BList blist => FormatList(blist, indent),
                BDictionary bdict => FormatDictionary(bdict, indent),
                _ => $"{indentStr}{obj}"
            };
        }

        private static string FormatList(BList list, int indent)
        {
            if (list.Count == 0) return "[]";

            var sb = new StringBuilder();
            var indentStr = new string(' ', indent * 2);

            sb.AppendLine("[");
            foreach (var item in list)
            {
                sb.AppendLine(item.ToDebugString(indent + 1));
            }
            sb.Append(indentStr).Append("]");

            return sb.ToString();
        }

        private static string FormatDictionary(BDictionary dict, int indent)
        {
            if (dict.Count == 0) return "{}";

            var sb = new StringBuilder();
            var indentStr = new string(' ', indent * 2);

            sb.AppendLine("{");
            foreach (var (key, value) in dict)
            {
                sb.Append(new string(' ', (indent + 1) * 2));
                sb.Append($"\"{key}\": ");
                sb.AppendLine(value.ToDebugString(indent + 1));
            }
            sb.Append(indentStr).Append("}");

            return sb.ToString();
        }
    }
}
