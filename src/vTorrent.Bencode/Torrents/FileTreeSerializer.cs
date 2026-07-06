using System;
using vTorrent.Bencode.Objects;

namespace vTorrent.Bencode.Torrents;

/// <summary>
/// Serializes FileTree back to BDictionary for .torrent file creation.
/// </summary>
public static class FileTreeSerializer
{
    public static BDictionary Serialize(FileTree tree)
    {
        if (tree is null) throw new ArgumentNullException(nameof(tree));
        return SerializeNode(tree.Root);
    }

    private static BDictionary SerializeNode(FileTreeNode node)
    {
        var dict = new BDictionary();

        if (node.IsFile)
        {
            var props = new BDictionary
            {
                ["length"] = new BNumber(node.Entry!.Length)
            };

            if (node.Entry.PiecesRoot is not null)
                props["pieces root"] = new BString(node.Entry.PiecesRoot.Value.Bytes);

            dict[""] = props;
        }
        else if (node.Children is not null)
        {
            foreach (var (name, child) in node.Children)
            {
                if (child.IsFile)
                {
                    // File node: wrapper dict containing "" props key
                    var fileDict = new BDictionary();
                    var props = new BDictionary
                    {
                        ["length"] = new BNumber(child.Entry!.Length)
                    };
                    if (child.Entry.PiecesRoot is not null)
                        props["pieces root"] = new BString(child.Entry.PiecesRoot.Value.Bytes);

                    fileDict[""] = props;
                    dict[name] = fileDict;
                }
                else
                {
                    dict[name] = SerializeNode(child);
                }
            }
        }

        return dict;
    }
}
