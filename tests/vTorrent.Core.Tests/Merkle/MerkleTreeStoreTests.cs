using FluentAssertions;
using System.Security.Cryptography;
using vTorrent.Bencode.Torrents;
using vTorrent.Core.Merkle;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Merkle;

public class MerkleTreeStoreTests : IDisposable
{
    private readonly string _tempDir;

    public MerkleTreeStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static SHA256Hash HashBlock(byte value)
        => new(SHA256.HashData(new[] { value }));

    private MerkleTree CreateTestTree(int leafCount)
    {
        var leaves = new SHA256Hash[leafCount];
        for (int i = 0; i < leafCount; i++)
            leaves[i] = HashBlock((byte)i);
        return MerkleTree.FromLeaves(leaves);
    }

    [Fact]
    public async Task SaveAndLoad_SingleTree_RoundTrips()
    {
        var tree = CreateTestTree(4);
        var store = new MerkleTreeStore(_tempDir);

        await store.SaveAsync("test_hash", new[] { tree });
        var loaded = await store.LoadAsync("test_hash", new[] { tree.Root });

        loaded.Should().HaveCount(1);
        loaded![0].Root.Should().Be(tree.Root);
        loaded[0].LeafCount.Should().Be(tree.LeafCount);
    }

    [Fact]
    public async Task SaveAndLoad_MultipleTrees_RoundTrips()
    {
        var tree1 = CreateTestTree(2);
        var tree2 = CreateTestTree(8);
        var store = new MerkleTreeStore(_tempDir);

        await store.SaveAsync("multi", new[] { tree1, tree2 });
        var loaded = await store.LoadAsync("multi", new[] { tree1.Root, tree2.Root });

        loaded.Should().HaveCount(2);
        loaded![0].Root.Should().Be(tree1.Root);
        loaded[1].Root.Should().Be(tree2.Root);
    }

    [Fact]
    public async Task Load_WrongRoot_Throws()
    {
        var tree = CreateTestTree(4);
        var store = new MerkleTreeStore(_tempDir);

        await store.SaveAsync("bad_root", new[] { tree });

        var wrongRoot = HashBlock(0xFF);
        var act = () => store.LoadAsync("bad_root", new[] { wrongRoot });

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        var store = new MerkleTreeStore(_tempDir);
        var result = await store.LoadAsync("nonexistent", new[] { HashBlock(1) });
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_CreatesFileOnDisk()
    {
        var tree = CreateTestTree(2);
        var store = new MerkleTreeStore(_tempDir);

        await store.SaveAsync("disk_check", new[] { tree });

        var filePath = Path.Combine(_tempDir, "disk_check.tree");
        File.Exists(filePath).Should().BeTrue();
    }
}
