using System;
using System.Collections.Generic;
using vTorrent.Bencode.Objects;

namespace vTorrent.Bencode.Torrents;

/// <summary>
/// Parses BEP 52 "file tree" bencoded dictionaries into FileTree structures.
/// Also provides Flatten() to convert v2 file tree to v1-compatible TorrentFile list.
/// </summary>
public static class FileTreeParser
{
    /// <summary>
    /// Parse a bencoded "file tree" dictionary into a FileTree.
    /// </summary>
    public static FileTree Parse(BDictionary dict)
    {
        if (dict is null) throw new ArgumentNullException(nameof(dict));
        var children = ParseChildren(dict);
        var root = FileTreeNode.Directory("", children);
        return new FileTree(root);
    }

    /// <summary>
    /// Flatten a FileTree into a list of TorrentFile (v1-compatible).
    /// Files are returned in sorted order (BEP 52 requires UTF-8 sorted keys).
    /// </summary>
    public static IReadOnlyList<TorrentFile> Flatten(FileTree tree)
    {
        var files = new List<TorrentFile>();
        FlattenNode(tree.Root, new List<string>(), files);
        return files.AsReadOnly();
    }

    private static IReadOnlyDictionary<string, FileTreeNode> ParseChildren(BDictionary dict)
    {
        var children = new SortedDictionary<string, FileTreeNode>(StringComparer.Ordinal);

        foreach (var kvp in dict)
        {
            var key = kvp.Key.ToString();
            if (kvp.Value is not BDictionary childDict)
                continue;

            if (key == "")
            {
                // This is a file property entry — handled by the parent
                continue;
            }

            // Check if this entry is a file (has empty-string key with length)
            if (childDict.ContainsKey(""))
            {
                var propsDict = childDict.GetDictionary("");
                var length = propsDict.GetNumber("length");

                SHA256Hash? piecesRoot = null;
                var rootBytes = propsDict.GetOrDefault<BString>("pieces root");
                if (rootBytes is not null && rootBytes.Value.Length == SHA256Hash.Size)
                    piecesRoot = new SHA256Hash(rootBytes.Value.ToArray());

                var entry = new FileTreeEntry(length, piecesRoot);
                children[key] = FileTreeNode.File(key, entry);
            }
            else
            {
                // It's a directory
                var subChildren = ParseChildren(childDict);
                children[key] = FileTreeNode.Directory(key, subChildren);
            }
        }

        return children;
    }

    private static void FlattenNode(
        FileTreeNode node,
        List<string> currentPath,
        List<TorrentFile> files)
    {
        if (node.IsFile)
        {
            files.Add(new TorrentFile
            {
                Path = currentPath.ToArray(),
                Length = node.Entry!.Length,
                PiecesRoot = node.Entry.PiecesRoot
            });
            return;
        }

        if (node.Children is null) return;

        foreach (var (name, child) in node.Children)
        {
            currentPath.Add(name);
            FlattenNode(child, currentPath, files);
            currentPath.RemoveAt(currentPath.Count - 1);
        }
    }
}
