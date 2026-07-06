// src/vTorrent.CLI/Client/VTorrentClient.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using vTorrent.Abstractions.Models;
using vTorrent.Cli.Profiles;

namespace vTorrent.Cli.Client;

public class VTorrentClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TokenStore _tokenStore;
    private readonly string _profileName;
    private readonly ProfileEntry _profile;
    private readonly bool _forceJwt;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public VTorrentClient(ProfileEntry profile, string profileName, TokenStore tokenStore,
        bool insecure = false, string? caCertPath = null, int timeoutSeconds = 30, bool forceJwt = false)
    {
        _profile = profile;
        _profileName = profileName;
        _tokenStore = tokenStore;

        var handler = new HttpClientHandler();
        if (insecure || profile.Insecure)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{(profile.Https ? "https" : "http")}://{profile.Host}"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        _forceJwt = forceJwt;
        ApplyToken();
    }

    public VTorrentClient(HttpClient httpClient, TokenStore tokenStore, string profileName, bool forceJwt = false)
    {
        _http = httpClient;
        _tokenStore = tokenStore;
        _profileName = profileName;
        _profile = new ProfileEntry();
        _forceJwt = forceJwt;
        ApplyToken();
    }

    private void ApplyToken()
    {
        _http.DefaultRequestHeaders.Remove("Authorization");
        _http.DefaultRequestHeaders.Remove("X-API-Key");

        var token = _tokenStore.Load(_profileName);
        if (token == null) return;

        if (token.HasApiKey && !_forceJwt)
        {
            _http.DefaultRequestHeaders.Add("X-API-Key", token.ApiKey);
        }
        else if (!string.IsNullOrEmpty(token.AccessToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }
    }

    // --- Result pattern helpers ---

    private async Task<ApiResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            return ApiResult<T>.Success(result);
        }
        catch (ApiException ex)
        {
            return ApiResult<T>.Fail(ex.Message, ex.ErrorCode, ex.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Fail($"Connection failed: {ex.Message}", "CONNECTION_ERROR");
        }
        catch (TaskCanceledException)
        {
            return ApiResult<T>.Fail("Request timed out", "TIMEOUT");
        }
    }

    private async Task<ApiResult<bool>> ExecuteVoidAsync(Func<Task> action)
    {
        try
        {
            await action();
            return ApiResult<bool>.Success(true);
        }
        catch (ApiException ex)
        {
            return ApiResult<bool>.Fail(ex.Message, ex.ErrorCode, ex.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<bool>.Fail($"Connection failed: {ex.Message}", "CONNECTION_ERROR");
        }
        catch (TaskCanceledException)
        {
            return ApiResult<bool>.Fail("Request timed out", "TIMEOUT");
        }
    }

    // --- Hash prefix resolution ---

    public async Task<string> ResolveHashAsync(string hashPrefix)
    {
        if (hashPrefix.Length >= 40) return hashPrefix;

        // Fetch all torrents and match by prefix
        var response = await _http.GetAsync("/api/v1/torrents");
        await EnsureSuccessAsync(response);
        var torrents = await response.Content.ReadFromJsonAsync<List<TorrentSnapshot>>(JsonOpts) ?? new();

        var matches = torrents.Where(t =>
            t.InfoHash.StartsWith(hashPrefix, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
            throw new ApiException(404, "TORRENT_NOT_FOUND", $"No torrent matches prefix: {hashPrefix}");
        if (matches.Count > 1)
            throw new ApiException(400, "AMBIGUOUS_HASH", $"Multiple torrents match prefix: {hashPrefix}");

        return matches[0].InfoHash;
    }

    // --- Auth ---

    public Task<ApiResult<(string accessToken, string refreshToken, int expiresIn)>> LoginAsync(string username, string password)
        => ExecuteAsync(async () =>
        {
            var response = await _http.PostAsJsonAsync("/auth/login", new { username, password });
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts);
            return (
                result!["accessToken"]!.GetValue<string>(),
                result["refreshToken"]!.GetValue<string>(),
                result["expiresIn"]!.GetValue<int>()
            );
        });

    public Task<ApiResult<bool>> LogoutAsync(string refreshToken)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsJsonAsync("/auth/logout", new { refreshToken });
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> ChangePasswordAsync(string currentPassword, string newPassword)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsJsonAsync("/auth/change-password",
                new { currentPassword, newPassword });
            await EnsureSuccessAsync(response);
        });

    // --- Torrents ---

    public Task<ApiResult<List<TorrentSnapshot>>> ListTorrentsAsync(
        string? phase = null, string? category = null, string? tag = null,
        string? sort = null, int? limit = null, int? offset = null)
        => ExecuteAsync(async () =>
        {
            var query = BuildQuery(
                ("phase", phase), ("category", category), ("tag", tag),
                ("sort", sort), ("limit", limit?.ToString()), ("offset", offset?.ToString()));
            var response = await _http.GetAsync($"/api/v1/torrents{query}");
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<List<TorrentSnapshot>>(JsonOpts) ?? new();
        });

    public Task<ApiResult<TorrentSnapshot?>> GetTorrentAsync(string hash)
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync($"/api/v1/torrents/{hash}");
            if (response.StatusCode == HttpStatusCode.NotFound) return (TorrentSnapshot?)null;
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<TorrentSnapshot>(JsonOpts);
        });

    public Task<ApiResult<ManagedTorrentView?>> GetTorrentDetailsAsync(string hash)
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync($"/api/v1/torrents/{hash}/details");
            if (response.StatusCode == HttpStatusCode.NotFound) return (ManagedTorrentView?)null;
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<ManagedTorrentView>(JsonOpts);
        });

    public Task<ApiResult<string>> AddTorrentFileAsync(string filePath, string? savePath = null, bool paused = false,
        bool sequential = false, bool firstLastPriority = false, bool topOfQueue = false)
        => ExecuteAsync(async () =>
        {
            if (!File.Exists(filePath))
                throw new ApiException(400, "FILE_NOT_FOUND", $"File not found: {filePath}");

            using var form = new MultipartFormDataContent();
            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
            form.Add(fileContent, "torrentFile", Path.GetFileName(filePath));

            var options = new JsonObject
            {
                ["startImmediately"] = !paused,
                ["sequentialDownload"] = sequential,
                ["firstLastPiecePriority"] = firstLastPriority,
                ["addToTopOfQueue"] = topOfQueue
            };
            if (savePath != null) options["savePath"] = savePath;
            form.Add(new StringContent(options.ToJsonString()), "options");

            var response = await _http.PostAsync("/api/v1/torrents", form);
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts);
            return result!["infoHash"]!.GetValue<string>();
        });

    public Task<ApiResult<string>> AddMagnetAsync(string magnetUri, string? savePath = null, bool paused = false,
        bool sequential = false, bool firstLastPriority = false, bool topOfQueue = false)
        => ExecuteAsync(async () =>
        {
            var body = new JsonObject
            {
                ["magnetUri"] = magnetUri,
                ["startImmediately"] = !paused,
                ["sequentialDownload"] = sequential,
                ["firstLastPiecePriority"] = firstLastPriority,
                ["addToTopOfQueue"] = topOfQueue
            };
            if (savePath != null) body["savePath"] = savePath;

            var response = await _http.PostAsJsonAsync("/api/v1/torrents/magnet", body);
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts);
            return result!["infoHash"]!.GetValue<string>();
        });

    public Task<ApiResult<bool>> PauseAsync(string hash)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync($"/api/v1/torrents/{hash}/pause", null);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> ResumeAsync(string hash)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync($"/api/v1/torrents/{hash}/resume", null);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<DeleteResult?>> RemoveAsync(string hash, bool deleteFiles = false, bool secureWipe = false, bool wipeMetadata = false)
        => ExecuteAsync(async () =>
        {
            var query = BuildQuery(
                ("deleteFiles", deleteFiles ? "true" : null),
                ("secureWipe", secureWipe ? "true" : null),
                ("wipeMetadata", wipeMetadata ? "true" : null));
            var response = await _http.DeleteAsync($"/api/v1/torrents/{hash}{query}");
            if (response.StatusCode == HttpStatusCode.NotFound) return (DeleteResult?)null;
            await EnsureSuccessAsync(response);
            try
            {
                return await response.Content.ReadFromJsonAsync<DeleteResult>(JsonOpts);
            }
            catch
            {
                return null;
            }
        });

    public Task<ApiResult<bool>> PauseAllAsync()
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync("/api/v1/torrents/pause-all", null);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> ResumeAllAsync()
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync("/api/v1/torrents/resume-all", null);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> ForceStartAsync(string hash)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync($"/api/v1/torrents/{hash}/force-start", null);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> RecheckAsync(string hash)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync($"/api/v1/torrents/{hash}/recheck", null);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> ToggleSuperSeedAsync(string hash)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync($"/api/v1/torrents/{hash}/super-seed", null);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> MoveAsync(string hash, string savePath)
        => ExecuteAsync(async () =>
        {
            var response = await _http.PostAsJsonAsync($"/api/v1/torrents/{hash}/location", new { savePath });
            if ((int)response.StatusCode == 409) return false;
            await EnsureSuccessAsync(response);
            return true;
        });

    // --- Torrent Config ---

    public Task<ApiResult<bool>> SetTorrentSettingsAsync(string hash, object settings)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/v1/torrents/{hash}/settings", settings);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> SetFilePrioritiesAsync(string hash, IList<object> priorities)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/v1/torrents/{hash}/files/priorities", new { priorities });
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> QueueActionAsync(string hash, string action)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync($"/api/v1/torrents/{hash}/queue/{action}", null);
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> SetCategoryAsync(string hash, int? categoryId)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/v1/torrents/{hash}/category", new { categoryId });
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<List<JsonObject>>> GetTorrentTagsAsync(string hash)
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync($"/api/v1/torrents/{hash}/tags");
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<List<JsonObject>>(JsonOpts) ?? new();
        });

    public Task<ApiResult<bool>> SetTorrentTagsAsync(string hash, IEnumerable<int> tagIds)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/v1/torrents/{hash}/tags", new { tagIds });
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool[]?>> GetPieceStatesAsync(string hash)
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync($"/api/v1/torrents/{hash}/pieces");
            if (response.StatusCode == HttpStatusCode.NotFound) return (bool[]?)null;
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<bool[]>(JsonOpts);
        });

    // --- Session ---

    public Task<ApiResult<SessionStatistics>> GetStatsAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/session/stats");
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<SessionStatistics>(JsonOpts))!;
        });

    public Task<ApiResult<JsonObject>> GetSessionCountsAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/session/counts");
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts))!;
        });

    public Task<ApiResult<JsonObject>> GetSessionSettingsAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/session/settings");
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts))!;
        });

    public Task<ApiResult<bool>> UpdateSessionSettingsAsync(JsonObject settings)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/v1/session/settings", new { settings });
            await EnsureSuccessAsync(response);
        });

    // --- Categories ---

    public Task<ApiResult<List<JsonObject>>> GetCategoriesAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/categories");
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<List<JsonObject>>(JsonOpts) ?? new();
        });

    public Task<ApiResult<JsonObject>> CreateCategoryAsync(string name, string? color, string? savePath)
        => ExecuteAsync(async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/v1/categories", new { name, color, savePath });
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts))!;
        });

    public Task<ApiResult<bool>> UpdateCategoryAsync(int id, string name, string? color, string? savePath)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/v1/categories/{id}", new { name, color, savePath });
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> DeleteCategoryAsync(int id)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.DeleteAsync($"/api/v1/categories/{id}");
            await EnsureSuccessAsync(response);
        });

    // --- Tags ---

    public Task<ApiResult<List<JsonObject>>> GetTagsAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/tags");
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<List<JsonObject>>(JsonOpts) ?? new();
        });

    public Task<ApiResult<JsonObject>> CreateTagAsync(string name, string? color)
        => ExecuteAsync(async () =>
        {
            var response = await _http.PostAsJsonAsync("/api/v1/tags", new { name, color });
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts))!;
        });

    public Task<ApiResult<bool>> UpdateTagAsync(int id, string name, string? color)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync($"/api/v1/tags/{id}", new { name, color });
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<bool>> DeleteTagAsync(int id)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.DeleteAsync($"/api/v1/tags/{id}");
            await EnsureSuccessAsync(response);
        });

    // --- DHT ---

    public Task<ApiResult<JsonObject>> GetDhtStatusAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/dht");
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts))!;
        });

    public Task<ApiResult<bool>> ToggleDhtAsync()
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PostAsync("/api/v1/dht/toggle", null);
            await EnsureSuccessAsync(response);
        });

    // --- Schedule ---

    public Task<ApiResult<byte[]>> ExportScheduleAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/profiles/schedule/export");
            await EnsureSuccessAsync(response);
            return await response.Content.ReadAsByteArrayAsync();
        });

    public Task<ApiResult<vTorrent.Abstractions.Settings.ScheduleImportResult>> ImportScheduleAsync(string filePath)
        => ExecuteAsync(async () =>
        {
            using var fileStream = File.OpenRead(filePath);
            using var form = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(streamContent, "file", Path.GetFileName(filePath));

            var response = await _http.PostAsync("/api/v1/profiles/schedule/import", form);
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<vTorrent.Abstractions.Settings.ScheduleImportResult>(JsonOpts))!;
        });

    // --- Profiles ---

    public Task<ApiResult<List<ProfileMeta>>> GetProfilesAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/profiles");
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<List<ProfileMeta>>(JsonOpts) ?? new();
        });

    public Task<ApiResult<ActiveProfileState>> GetActiveProfileAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/profiles/active");
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<ActiveProfileState>(JsonOpts))!;
        });

    public Task<ApiResult<bool>> ActivateProfileAsync(string name)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/v1/profiles/active", new { name });
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<ScheduleData>> GetScheduleAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/api/v1/profiles/schedule");
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<ScheduleData>(JsonOpts))!;
        });

    public Task<ApiResult<bool>> ToggleScheduleAsync(bool enable)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.PutAsJsonAsync("/api/v1/profiles/schedule/toggle", new { enabled = enable });
            await EnsureSuccessAsync(response);
        });

    public Task<ApiResult<byte[]>> ExportProfileAsync(string name)
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync($"/api/v1/profiles/{Uri.EscapeDataString(name)}/export");
            await EnsureSuccessAsync(response);
            return await response.Content.ReadAsByteArrayAsync();
        });

    public Task<ApiResult<ProfileImportResult>> ImportProfileAsync(string filePath)
        => ExecuteAsync(async () =>
        {
            using var fileStream = File.OpenRead(filePath);
            using var form = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(streamContent, "file", Path.GetFileName(filePath));

            var response = await _http.PostAsync("/api/v1/profiles/import", form);
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<ProfileImportResult>(JsonOpts))!;
        });

    // --- API Keys ---

    public Task<ApiResult<JsonObject>> CreateApiKeyAsync(string label)
        => ExecuteAsync(async () =>
        {
            var response = await _http.PostAsJsonAsync("/auth/api-keys", new { label });
            await EnsureSuccessAsync(response);
            return (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts))!;
        });

    public Task<ApiResult<List<JsonObject>>> GetApiKeysAsync()
        => ExecuteAsync(async () =>
        {
            var response = await _http.GetAsync("/auth/api-keys");
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<List<JsonObject>>(JsonOpts) ?? new();
        });

    public Task<ApiResult<bool>> RevokeApiKeyAsync(string keyPrefix)
        => ExecuteVoidAsync(async () =>
        {
            var response = await _http.DeleteAsync($"/auth/api-keys/{keyPrefix}");
            await EnsureSuccessAsync(response);
        });

    // --- Helpers ---

    private async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        try
        {
            var error = JsonSerializer.Deserialize<JsonObject>(body, JsonOpts);
            throw new ApiException(
                (int)response.StatusCode,
                error?["code"]?.GetValue<string>() ?? "UNKNOWN",
                error?["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed");
        }
        catch (JsonException)
        {
            throw new ApiException((int)response.StatusCode, "UNKNOWN", body);
        }
    }

    private static string BuildQuery(params (string key, string? value)[] pairs)
    {
        var parts = new List<string>();
        foreach (var (key, value) in pairs)
            if (value != null) parts.Add($"{key}={Uri.EscapeDataString(value)}");
        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }

    public void Dispose() => _http.Dispose();
}
