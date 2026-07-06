using Xunit;
using vTorrent.Abstractions.Enums;
using vTorrent.Desktop.ViewModels;

namespace vTorrent.Tests.Unit.ViewModels;

public class TorrentFileTreeNodeViewModelTests
{
    [Fact]
    public void Checking_folder_checks_all_children()
    {
        var folder = new TorrentFileTreeNodeViewModel("movies", isFolder: true);
        var file1 = new TorrentFileTreeNodeViewModel("movie1.mkv", isFolder: false, sizeBytes: 1000, fileIndex: 0);
        var file2 = new TorrentFileTreeNodeViewModel("movie2.mkv", isFolder: false, sizeBytes: 2000, fileIndex: 1);
        folder.Children.Add(file1);
        folder.Children.Add(file2);
        file1.Parent = folder;
        file2.Parent = folder;

        // Start unchecked
        file1.IsChecked = false;
        file2.IsChecked = false;

        // Check the folder
        folder.IsChecked = true;

        Assert.True(file1.IsChecked);
        Assert.True(file2.IsChecked);
    }

    [Fact]
    public void Unchecking_folder_unchecks_all_children()
    {
        var folder = new TorrentFileTreeNodeViewModel("movies", isFolder: true);
        var file1 = new TorrentFileTreeNodeViewModel("movie1.mkv", isFolder: false, sizeBytes: 1000, fileIndex: 0);
        folder.Children.Add(file1);
        file1.Parent = folder;
        file1.IsChecked = true;

        folder.IsChecked = false;

        Assert.False(file1.IsChecked);
    }

    [Fact]
    public void Mixed_children_sets_folder_indeterminate()
    {
        var folder = new TorrentFileTreeNodeViewModel("movies", isFolder: true);
        var file1 = new TorrentFileTreeNodeViewModel("movie1.mkv", isFolder: false, sizeBytes: 1000, fileIndex: 0);
        var file2 = new TorrentFileTreeNodeViewModel("movie2.mkv", isFolder: false, sizeBytes: 2000, fileIndex: 1);
        folder.Children.Add(file1);
        folder.Children.Add(file2);
        file1.Parent = folder;
        file2.Parent = folder;

        file1.IsChecked = true;
        file2.IsChecked = false;

        // Folder should be null (indeterminate) when children are mixed
        Assert.Null(folder.IsChecked);
    }

    [Fact]
    public void Priority_defaults_to_Normal()
    {
        var file = new TorrentFileTreeNodeViewModel("test.txt", isFolder: false, sizeBytes: 100, fileIndex: 0);
        Assert.Equal(FilePriority.Normal, file.Priority);
        Assert.True(file.IsChecked);
    }

    [Fact]
    public void Setting_priority_to_Skip_unchecks_file()
    {
        var file = new TorrentFileTreeNodeViewModel("test.txt", isFolder: false, sizeBytes: 100, fileIndex: 0);
        file.Priority = FilePriority.Skip;
        Assert.False(file.IsChecked);
    }

    [Fact]
    public void Unchecking_file_sets_priority_to_Skip()
    {
        var file = new TorrentFileTreeNodeViewModel("test.txt", isFolder: false, sizeBytes: 100, fileIndex: 0);
        file.IsChecked = false;
        Assert.Equal(FilePriority.Skip, file.Priority);
    }

    [Fact]
    public void SelectedSize_sums_checked_files()
    {
        var root = new TorrentFileTreeNodeViewModel("root", isFolder: true);
        var file1 = new TorrentFileTreeNodeViewModel("a.txt", isFolder: false, sizeBytes: 1000, fileIndex: 0);
        var file2 = new TorrentFileTreeNodeViewModel("b.txt", isFolder: false, sizeBytes: 2000, fileIndex: 1);
        root.Children.Add(file1);
        root.Children.Add(file2);
        file1.Parent = root;
        file2.Parent = root;

        file1.IsChecked = true;
        file2.IsChecked = false;

        Assert.Equal(1000, root.GetSelectedSizeBytes());
    }
}
