using System.Threading.Tasks;
using vTorrent.Abstractions.Records;

namespace vTorrent.Abstractions.Interfaces.Auth;

public interface IApiKeyValidator
{
    Task<bool> ValidateAsync(string apiKey);
    Task<ApiKeyInfo?> GetInfoAsync(string apiKey);
}
