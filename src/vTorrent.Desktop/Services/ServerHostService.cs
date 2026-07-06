using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Orchestration;
using vTorrent.Core.Persistence;
using vTorrent.Core.Settings;

namespace vTorrent.Desktop.Services;

public enum ServerStatus
{
    Stopped,
    Starting,
    Running,
    Restarting,
    Error
}

public class ServerHostService
{
    private readonly IOptionsMonitor<ServerSettings> _serverMonitor;
    private readonly IOptionsMonitor<ConnectionSettings> _connectionMonitor;
    private readonly SessionPersistence _persistence;
    private readonly ITorrentService _torrentService;
    private readonly SettingsManager _settingsManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ServerHostService> _logger;
    private readonly ProfileManager _profileManager;
    private readonly TorrentOrchestrator _orchestrator;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private readonly IDisposable _onChangeDisposable = null!;

    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private ServerSettings _previousSettings;

    public ServerStatus Status { get; private set; } = ServerStatus.Stopped;
    public string? ListeningUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event EventHandler? StatusChanged;

    public ServerHostService(
        IOptionsMonitor<ServerSettings> serverMonitor,
        IOptionsMonitor<ConnectionSettings> connectionMonitor,
        SessionPersistence persistence,
        ITorrentService torrentService,
        ProfileManager profileManager,
        TorrentOrchestrator orchestrator,
        ILoggerFactory loggerFactory)
    {
        _serverMonitor = serverMonitor;
        _connectionMonitor = connectionMonitor;
        _persistence = persistence;
        _torrentService = torrentService;
        _profileManager = profileManager;
        _orchestrator = orchestrator;
        _settingsManager = persistence.SettingsManager!;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ServerHostService>();
        _previousSettings = SnapshotSettings(serverMonitor.CurrentValue);

        _onChangeDisposable = _serverMonitor.OnChange(OnSettingsChanged);
    }

    public async Task InitializeAsync()
    {
        var settings = _serverMonitor.CurrentValue;
        if (settings.Enabled)
        {
            await StartServerAsync(settings);
        }
    }

    public async Task StopAsync()
    {
        _onChangeDisposable.Dispose();
        await _lifecycleLock.WaitAsync();
        try
        {
            await StopServerInternalAsync();
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }

    public void OpenBrowser()
    {
        if (Status != ServerStatus.Running || string.IsNullOrEmpty(ListeningUrl))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(ListeningUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open browser for {Url}", ListeningUrl);
        }
    }

    private async void OnSettingsChanged(ServerSettings newSettings)
    {
        try
        {
            await _lifecycleLock.WaitAsync();
            var prev = _previousSettings;
            _previousSettings = SnapshotSettings(newSettings);

            if (prev.Enabled && !newSettings.Enabled)
            {
                await StopServerInternalAsync();
            }
            else if (!prev.Enabled && newSettings.Enabled)
            {
                await StartServerAsync(newSettings);
            }
            else if (newSettings.Enabled && NeedsRestart(prev, newSettings))
            {
                SetStatus(ServerStatus.Restarting);
                await StopServerInternalAsync();
                await StartServerAsync(newSettings);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling server settings change");
            SetStatus(ServerStatus.Error, errorMessage: ex.Message);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartServerAsync(ServerSettings settings)
    {
        SetStatus(ServerStatus.Starting);

        try
        {
            var connection = _persistence.Connection;
            if (connection == null)
            {
                SetStatus(ServerStatus.Error, errorMessage: "Database connection not available");
                return;
            }

            _serverCts = new CancellationTokenSource();
            var ct = _serverCts.Token;

            var webRootPath = string.IsNullOrEmpty(settings.WebUIBundlePath) ? null : settings.WebUIBundlePath;

            _serverTask = Task.Run(async () =>
            {
                await vTorrent.Server.Program.StartAsync(
                    connection,
                    _torrentService,
                    _settingsManager,
                    settings,
                    _connectionMonitor.CurrentValue,
                    _serverMonitor,
                    _loggerFactory,
                    _profileManager,
                    _orchestrator.Scheduler,
                    webRootPath,
                    ct);
            }, ct);

            var scheme = settings.EnableHttps ? "https" : "http";
            var url = $"{scheme}://{settings.ListenAddress}:{settings.ListenPort}";

            if (await WaitForServerAsync(url, ct))
            {
                ListeningUrl = url;
                SetStatus(ServerStatus.Running);

                if (settings.OpenBrowserOnServerStart)
                    OpenBrowser();
            }
            else
            {
                SetStatus(ServerStatus.Error, errorMessage: "Server failed to start within timeout");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start web server");
            SetStatus(ServerStatus.Error, errorMessage: ex.Message);
        }
    }

    private async Task StopServerInternalAsync()
    {
        if (_serverCts == null)
        {
            SetStatus(ServerStatus.Stopped);
            return;
        }

        try
        {
            _serverCts.Cancel();

            if (_serverTask != null)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _serverTask.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Server task did not complete cleanly");
                }
            }
        }
        finally
        {
            _serverCts?.Dispose();
            _serverCts = null;
            _serverTask = null;
            ListeningUrl = null;
            ErrorMessage = null;
            SetStatus(ServerStatus.Stopped);
        }
    }

    private static async Task<bool> WaitForServerAsync(string url, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

        for (int i = 0; i < 10; i++)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                await Task.Delay(500, ct);
                var response = await client.GetAsync(url, ct);
                return true;
            }
            catch { }
        }
        return false;
    }

    private static bool NeedsRestart(ServerSettings prev, ServerSettings curr)
    {
        return prev.ListenPort != curr.ListenPort
            || prev.ListenAddress != curr.ListenAddress
            || prev.EnableHttps != curr.EnableHttps
            || prev.HttpsCertPath != curr.HttpsCertPath
            || prev.HttpsCertPassword != curr.HttpsCertPassword
            || prev.WebUIBundlePath != curr.WebUIBundlePath
            || prev.JwtSecret != curr.JwtSecret
            || prev.OidcAuthority != curr.OidcAuthority
            || prev.OidcClientId != curr.OidcClientId
            || prev.OidcClientSecret != curr.OidcClientSecret
            || prev.AllowedOrigins != curr.AllowedOrigins;
    }

    private static ServerSettings SnapshotSettings(ServerSettings s) => new()
    {
        Enabled = s.Enabled,
        ListenPort = s.ListenPort,
        ListenAddress = s.ListenAddress,
        EnableHttps = s.EnableHttps,
        HttpsCertPath = s.HttpsCertPath,
        HttpsCertPassword = s.HttpsCertPassword,
        LocalUsername = s.LocalUsername,
        LocalPasswordHash = s.LocalPasswordHash,
        JwtSecret = s.JwtSecret,
        JwtAccessTokenLifetimeMinutes = s.JwtAccessTokenLifetimeMinutes,
        JwtRefreshTokenLifetimeDays = s.JwtRefreshTokenLifetimeDays,
        OidcAuthority = s.OidcAuthority,
        OidcClientId = s.OidcClientId,
        OidcClientSecret = s.OidcClientSecret,
        OidcAllowedEmail = s.OidcAllowedEmail,
        AllowedOrigins = s.AllowedOrigins,
        OpenBrowserOnServerStart = s.OpenBrowserOnServerStart,
        WebUIBundlePath = s.WebUIBundlePath,
    };

    private void SetStatus(ServerStatus status, string? errorMessage = null)
    {
        Status = status;
        ErrorMessage = errorMessage;
        if (status == ServerStatus.Error)
            _logger.LogError("Server status: {Status} - {Error}", status, errorMessage);
        else
            _logger.LogInformation("Server status: {Status}", status);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
