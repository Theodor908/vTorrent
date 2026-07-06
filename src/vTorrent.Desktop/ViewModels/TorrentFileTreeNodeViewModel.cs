using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Enums;
using vTorrent.Core.Session;

namespace vTorrent.Desktop.ViewModels;

/// <summary>
/// Hierarchical file tree node for file selection in Add Torrent / Magnet dialogs.
/// Supports tri-state checkboxes and per-file priority (maps to FilePriority enum).
/// </summary>
public partial class TorrentFileTreeNodeViewModel : ObservableObject
{
    private bool _suppressPropagation;

    public string Name { get; }
    public bool IsFolder { get; }
    public long SizeBytes { get; }
    public int FileIndex { get; } // zero-based, for BEP 53 mapping; -1 for folders
    public string FullPath { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isVisible = true; // for search filtering

    /// <summary>
    /// Tri-state: true = all checked, false = all unchecked, null = indeterminate (folders only)
    /// </summary>
    [ObservableProperty]
    private bool? _isChecked = true;

    [ObservableProperty]
    private FilePriority _priority = FilePriority.Normal;

    public ObservableCollection<TorrentFileTreeNodeViewModel> Children { get; } = new();
    public TorrentFileTreeNodeViewModel? Parent { get; set; }

    /// <summary>
    /// Callback invoked on the root node when any descendant's check state changes.
    /// Used by the owning ViewModel to update selected size text.
    /// </summary>
    public Action? OnSelectionChanged { get; set; }

    public TorrentFileTreeNodeViewModel(string name, bool isFolder, long sizeBytes = 0, int fileIndex = -1)
    {
        Name = name;
        IsFolder = isFolder;
        SizeBytes = sizeBytes;
        FileIndex = fileIndex;
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_suppressPropagation) return;

        _suppressPropagation = true;
        try
        {
            if (IsFolder && value.HasValue)
            {
                // Propagate down recursively to ALL descendants
                SetCheckedRecursive(this, value.Value);
            }

            if (!IsFolder)
            {
                // Sync priority with check state
                if (value == false && Priority != FilePriority.Skip)
                    Priority = FilePriority.Skip;
                else if (value == true && Priority == FilePriority.Skip)
                    Priority = FilePriority.Normal;
            }

            // Propagate up: update parent's tri-state
            UpdateParentCheckState();

            // Notify root's callback so ViewModel can update selected size
            NotifySelectionChanged();
        }
        finally
        {
            _suppressPropagation = false;
        }
    }

    private void NotifySelectionChanged()
    {
        // Walk up to root and invoke callback
        var node = this;
        while (node.Parent != null) node = node.Parent;
        node.OnSelectionChanged?.Invoke();
    }

    /// <summary>
    /// Recursively set IsChecked and Priority on all descendants.
    /// Uses direct field assignment to avoid triggering change handlers.
    /// </summary>
    private static void SetCheckedRecursive(TorrentFileTreeNodeViewModel folder, bool isChecked)
    {
        foreach (var child in folder.Children)
        {
            child._suppressPropagation = true;
            child.IsChecked = isChecked;

            if (child.IsFolder)
            {
                // Recurse into subfolders
                SetCheckedRecursive(child, isChecked);
            }
            else
            {
                // Leaf file: sync priority
                child.Priority = isChecked ? FilePriority.Normal : FilePriority.Skip;
            }

            child._suppressPropagation = false;
        }
    }

    partial void OnPriorityChanged(FilePriority value)
    {
        if (_suppressPropagation) return;

        _suppressPropagation = true;
        try
        {
            // Sync check state with priority
            var shouldBeChecked = value != FilePriority.Skip;
            if (IsChecked != shouldBeChecked)
                IsChecked = shouldBeChecked;
            UpdateParentCheckState();
        }
        finally
        {
            _suppressPropagation = false;
        }
    }

    private void UpdateParentCheckState()
    {
        if (Parent == null || !Parent.IsFolder) return;

        Parent._suppressPropagation = true;
        try
        {
            var checkedCount = Parent.Children.Count(c => c.IsChecked == true);
            var uncheckedCount = Parent.Children.Count(c => c.IsChecked == false);
            var total = Parent.Children.Count;

            if (checkedCount == total)
                Parent.IsChecked = true;
            else if (uncheckedCount == total)
                Parent.IsChecked = false;
            else
                Parent.IsChecked = null; // indeterminate

            // Recurse up
            Parent.UpdateParentCheckState();
        }
        finally
        {
            Parent._suppressPropagation = false;
        }
    }

    /// <summary>
    /// Sum of SizeBytes for all checked leaf (file) nodes in this subtree.
    /// </summary>
    public long GetSelectedSizeBytes()
    {
        if (!IsFolder)
            return IsChecked == true ? SizeBytes : 0;

        return Children.Sum(c => c.GetSelectedSizeBytes());
    }

    /// <summary>
    /// Build tree from flat list of file paths (e.g., from torrent metadata).
    /// </summary>
    public static TorrentFileTreeNodeViewModel BuildTree(
        string rootName,
        IEnumerable<(string fullPath, long sizeBytes, int fileIndex)> files)
    {
        var root = new TorrentFileTreeNodeViewModel(rootName, isFolder: true);

        foreach (var (fullPath, sizeBytes, fileIndex) in files)
        {
            var parts = fullPath.Split('/', '\\');
            var current = root;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var isLast = i == parts.Length - 1;

                var existing = current.Children.FirstOrDefault(c => c.Name == part);
                if (existing != null)
                {
                    current = existing;
                }
                else
                {
                    var node = isLast
                        ? new TorrentFileTreeNodeViewModel(part, isFolder: false, sizeBytes: sizeBytes, fileIndex: fileIndex)
                          { FullPath = fullPath, Size = FormatBytes(sizeBytes) }
                        : new TorrentFileTreeNodeViewModel(part, isFolder: true);
                    node.Parent = current;
                    current.Children.Add(node);
                    current = node;
                }
            }
        }

        // Auto-navigate into single-folder chains so the user sees files directly
        // e.g. TorrentName/Folder/SubFolder/files → flatten to SubFolder as root
        var result = root;
        while (result.Children.Count == 1 && result.Children[0].IsFolder)
        {
            result = result.Children[0];
        }
        result.Parent = null;
        return result;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F2} {units[unit]}";
    }
}
