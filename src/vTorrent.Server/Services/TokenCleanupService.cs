using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using vTorrent.Server.Auth;

namespace vTorrent.Server.Services;

public class TokenCleanupService : BackgroundService
{
    private readonly RefreshTokenRepository _refreshRepo;
    private readonly ApiKeyRepository _apiKeyRepo;
    private readonly ILogger<TokenCleanupService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public TokenCleanupService(RefreshTokenRepository refreshRepo, ApiKeyRepository apiKeyRepo, ILogger<TokenCleanupService> logger)
    {
        _refreshRepo = refreshRepo;
        _apiKeyRepo = apiKeyRepo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Token cleanup service started (interval: {Interval})", Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _refreshRepo.CleanupAsync(cutoff);
                var apiKeyCutoff = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeSeconds();
                await _apiKeyRepo.CleanupRevokedAsync(apiKeyCutoff);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during token cleanup");
            }
        }
    }
}
