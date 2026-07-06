using System;

using System.Collections.Generic;

using System.Threading;

using System.Threading.Tasks;

using vTorrent.Bencode.Torrents;

using vTorrent.Core;

using vTorrent.Core.Merkle;
using vTorrent.Core.Download;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>

/// Default implementation of IHashExchangeHandler.

/// Serves hash requests from local trees and validates incoming hashes.

/// </summary>

public sealed class HashExchangeHandler : IHashExchangeHandler

{

    private readonly IReadOnlyDictionary<SHA256Hash, MerkleTree> _trees;

    private readonly HashPicker? _hashPicker;

    public HashExchangeHandler(

        IReadOnlyDictionary<SHA256Hash, MerkleTree> trees,

        HashPicker? hashPicker = null)

    {

        _trees = trees ?? throw new ArgumentNullException(nameof(trees));

        _hashPicker = hashPicker;

    }

    public async Task OnHashRequestAsync(IPeerConnection peer, HashRequestMessage msg, CancellationToken ct)

    {

        if (!_trees.TryGetValue(msg.PiecesRoot, out var tree))

        {

            await peer.SendHashRejectAsync(new HashRejectMessage

            {

                PiecesRoot = msg.PiecesRoot,

                BaseLayer = msg.BaseLayer,

                Index = msg.Index,

                Length = msg.Length,

                ProofLayers = msg.ProofLayers,

            }, ct).ConfigureAwait(false);

            return;

        }

        // Collect requested hashes from tree

        var firstLeaf = MerkleHelpers.FirstLeafIndex(tree.LeafCount);

        var hashes = new List<SHA256Hash>();

        // Data hashes at the requested layer

        for (int i = 0; i < msg.Length; i++)

        {

            var nodeIndex = firstLeaf + msg.Index + i;

            if (nodeIndex < tree.NodeCount)

                hashes.Add(tree.GetNode(nodeIndex));

            else

                hashes.Add(default);

        }

        // Proof (uncle) hashes

        if (msg.ProofLayers > 0 && hashes.Count > 0)

        {

            var proofNode = firstLeaf + msg.Index;

            var spanSize = msg.Length;

            var current = proofNode;

            // Skip up past the span (log2(length) levels)

            for (int level = 0; level < System.Numerics.BitOperations.Log2((uint)spanSize); level++)

                current = MerkleHelpers.Parent(current);

            // Add uncle hashes for proofLayers levels

            for (int p = 0; p < msg.ProofLayers && current > 0; p++)

            {

                hashes.Add(tree.GetNode(MerkleHelpers.Sibling(current)));

                current = MerkleHelpers.Parent(current);

            }

        }

        await peer.SendHashesAsync(new HashesMessage

        {

            PiecesRoot = msg.PiecesRoot,

            BaseLayer = msg.BaseLayer,

            Index = msg.Index,

            Length = msg.Length,

            ProofLayers = msg.ProofLayers,

            Hashes = hashes.ToArray(),

        }, ct).ConfigureAwait(false);

    }

    public Task OnHashesReceivedAsync(IPeerConnection peer, HashesMessage msg, CancellationToken ct)

    {

        if (!_trees.TryGetValue(msg.PiecesRoot, out var tree))

            return Task.CompletedTask;

        var dataCount = msg.Length;

        if (msg.Hashes.Count < dataCount)

            return Task.CompletedTask; // malformed

        var firstLeaf = MerkleHelpers.FirstLeafIndex(tree.LeafCount);

        var uncleStartIdx = dataCount;

        var uncleCount = msg.Hashes.Count - dataCount;

        if (uncleCount > 0)

        {

            // Compute subtree root from data hashes

            var dataSpan = new SHA256Hash[dataCount];

            for (int i = 0; i < dataCount; i++)

                dataSpan[i] = msg.Hashes[i];

            var subtreeHash = ComputeSubtreeRoot(dataSpan.AsSpan());

            // Find the subtree node covering the data span

            var spanNodeIdx = firstLeaf + msg.Index;

            var spanSize = dataCount;

            while (spanSize > 1)

            {

                spanNodeIdx = MerkleHelpers.Parent(spanNodeIdx);

                spanSize /= 2;

            }

            // Walk uncle hashes upward, verify chain reaches root

            var current = subtreeHash;

            for (int u = 0; u < uncleCount && spanNodeIdx > 0; u++)

            {

                var uncle = msg.Hashes[uncleStartIdx + u];

                var isLeftChild = (spanNodeIdx % 2 == 1);

                current = isLeftChild

                    ? HashPair(current, uncle)

                    : HashPair(uncle, current);

                spanNodeIdx = MerkleHelpers.Parent(spanNodeIdx);

            }

            if (current != tree.Root)

                return Task.CompletedTask; // proof failed

        }

        // Proof passed — insert data hashes

        for (int i = 0; i < dataCount; i++)

        {

            var nodeIndex = firstLeaf + msg.Index + i;

            if (nodeIndex < tree.NodeCount)

                tree.SetNode(nodeIndex, msg.Hashes[i]);

        }

        _hashPicker?.MarkHashesReceived(msg.PiecesRoot, msg.Index, dataCount);

        return Task.CompletedTask;

    }

    public Task OnHashRejectAsync(IPeerConnection peer, HashRejectMessage msg, CancellationToken ct)

    {

        _hashPicker?.OnHashRejected(new HashRequestMessage

        {

            PiecesRoot = msg.PiecesRoot,

            BaseLayer = msg.BaseLayer,

            Index = msg.Index,

            Length = msg.Length,

            ProofLayers = msg.ProofLayers

        });

        return Task.CompletedTask;

    }

    private static SHA256Hash ComputeSubtreeRoot(ReadOnlySpan<SHA256Hash> hashes)

    {

        if (hashes.Length == 1) return hashes[0];

        var layer = hashes.ToArray();

        while (layer.Length > 1)

        {

            var next = new SHA256Hash[layer.Length / 2];

            for (int i = 0; i < next.Length; i++)

                next[i] = HashPair(layer[i * 2], layer[i * 2 + 1]);

            layer = next;

        }

        return layer[0];

    }

    private static SHA256Hash HashPair(SHA256Hash left, SHA256Hash right)

    {

        Span<byte> combined = stackalloc byte[64];

        left.AsSpan().CopyTo(combined);

        right.AsSpan().CopyTo(combined[32..]);

        return new SHA256Hash(System.Security.Cryptography.SHA256.HashData(combined));

    }

}
