using System;

namespace vTorrent.Core.Network.PortMapping;

/// <summary>
/// Minimal span-based SAX XML parser for UPnP responses.
/// Strips namespace prefixes from tag names.
/// </summary>
public static class SimpleXmlReader
{
    public enum XmlTokenType : byte { StartTag, EndTag, Text }

    public delegate void TokenCallback(XmlTokenType type, ReadOnlySpan<char> value);

    public static void Parse(ReadOnlySpan<char> xml, TokenCallback callback)
    {
        int pos = 0;
        while (pos < xml.Length)
        {
            int lt = xml.Slice(pos).IndexOf('<');
            if (lt < 0)
            {
                var remaining = xml.Slice(pos).Trim();
                if (remaining.Length > 0)
                    callback(XmlTokenType.Text, remaining);
                break;
            }

            if (lt > 0)
            {
                var text = xml.Slice(pos, lt).Trim();
                if (text.Length > 0)
                    callback(XmlTokenType.Text, text);
            }

            pos += lt + 1;

            if (pos >= xml.Length) break;

            if (xml[pos] == '?')
            {
                int piEnd = xml.Slice(pos).IndexOf("?>".AsSpan());
                pos += piEnd < 0 ? xml.Length - pos : piEnd + 2;
                continue;
            }
            if (pos + 2 < xml.Length && xml[pos] == '!' && xml[pos + 1] == '-' && xml[pos + 2] == '-')
            {
                int commentEnd = xml.Slice(pos).IndexOf("-->".AsSpan());
                pos += commentEnd < 0 ? xml.Length - pos : commentEnd + 3;
                continue;
            }

            int gt = xml.Slice(pos).IndexOf('>');
            if (gt < 0) break;

            var tagContent = xml.Slice(pos, gt);
            pos += gt + 1;

            bool isEnd = tagContent.Length > 0 && tagContent[0] == '/';
            bool isSelfClosing = tagContent.Length > 0 && tagContent[tagContent.Length - 1] == '/';

            if (isEnd)
            {
                var name = StripPrefix(tagContent.Slice(1).Trim());
                callback(XmlTokenType.EndTag, name);
            }
            else
            {
                var nameSpan = isSelfClosing ? tagContent.Slice(0, tagContent.Length - 1) : tagContent;
                int space = nameSpan.IndexOf(' ');
                if (space >= 0) nameSpan = nameSpan.Slice(0, space);
                var name = StripPrefix(nameSpan.Trim());

                callback(XmlTokenType.StartTag, name);
                if (isSelfClosing)
                    callback(XmlTokenType.EndTag, name);
            }
        }
    }

    private static ReadOnlySpan<char> StripPrefix(ReadOnlySpan<char> name)
    {
        int colon = name.IndexOf(':');
        return colon >= 0 ? name.Slice(colon + 1) : name;
    }
}
