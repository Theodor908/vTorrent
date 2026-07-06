using vTorrent.Core.Settings;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using vTorrent.Abstractions.Settings.Enums;

namespace vTorrent.Core.PeerCommunication.Encryption;

/// <summary>
/// Outcome of an MSE/PE negotiation.
/// </summary>
public sealed class MseResult
{
    /// <summary>True if MSE negotiation succeeded (DH exchange completed).</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Negotiated encryption level (Plaintext or RC4).</summary>
    public EncryptionLevel NegotiatedLevel { get; init; }

    /// <summary>RC4 cipher for outgoing data (null if plaintext or no encryption).</summary>
    public RC4? OutgoingCipher { get; init; }

    /// <summary>RC4 cipher for incoming data (null if plaintext or no encryption).</summary>
    public RC4? IncomingCipher { get; init; }

    /// <summary>Responder: buffered IA bytes sent by initiator during handshake.</summary>
    public byte[]? InitialPayload { get; init; }

    /// <summary>Initiator: true if BT handshake was embedded as IA data.</summary>
    public bool InitialPayloadSent { get; init; }

    /// <summary>Responder: the info hash identified via req2 lookup.</summary>
    public byte[]? IdentifiedInfoHash { get; init; }

    /// <summary>Creates a plaintext (no encryption) result.</summary>
    public static MseResult Plaintext() => new() { IsEncrypted = false, NegotiatedLevel = EncryptionLevel.Plaintext };
}
