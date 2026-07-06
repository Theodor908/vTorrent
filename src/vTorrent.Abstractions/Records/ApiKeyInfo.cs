namespace vTorrent.Abstractions.Records;

public record ApiKeyInfo(
    string KeyPrefix,
    string Label,
    long CreatedAt,
    long? LastUsed,
    bool IsRevoked
);
