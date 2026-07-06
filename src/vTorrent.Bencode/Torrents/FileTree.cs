using System.Collections.Generic;

namespace vTorrent.Bencode.Torrents;

/// <summary>
/// BEP 52 file tree root container.
/// </summary>
public sealed class FileTree
{
    public FileTreeNode Root { get; }

    public FileTree(FileTreeNode root)
    {
        Root = root ?? throw new System.ArgumentNullException(nameof(root));
    }
}

/// <summary>
/// A node in the BEP 52 file tree. Either a directory (has Children) or a file (has Entry).
/// </summary>
public sealed class FileTreeNode
{
    public string Name { get; }
    public IReadOnlyDictionary<string, FileTreeNode>? Children { get; }
    public FileTreeEntry? Entry { get; }

    public bool IsDirectory => Children is not null;
    public bool IsFile => Entry is not null;

    private FileTreeNode(string name, IReadOnlyDictionary<string, FileTreeNode>? children, FileTreeEntry? entry)
    {
        Name = name;
        Children = children;
        Entry = entry;
    }

    public static FileTreeNode Directory(string name, IReadOnlyDictionary<string, FileTreeNode> children)
        => new(name, children, null);

    public static FileTreeNode File(string name, FileTreeEntry entry)
        => new(name, null, entry);
}

/// <summary>
/// Leaf entry in the BEP 52 file tree: file length and optional merkle root.
/// </summary>
public sealed record FileTreeEntry(long Length, SHA256Hash? PiecesRoot);
