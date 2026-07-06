namespace vTorrent.Server.Models;

public record CreateApiKeyRequest(string Label);
public record CreateApiKeyResponse(string ApiKey, string KeyPrefix, string Label, long CreatedAt);
public record ApiKeyListItem(string KeyPrefix, string Label, long CreatedAt, long? LastUsed, bool IsRevoked);
