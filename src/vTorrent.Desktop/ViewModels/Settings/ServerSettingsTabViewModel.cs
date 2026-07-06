using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using vTorrent.Abstractions.Settings;
using vTorrent.Desktop.Services;

namespace vTorrent.Desktop.ViewModels.Settings;

public partial class ServerSettingsTabViewModel : SettingsTabViewModelBase
{
    private readonly ServerHostService? _serverHost;
    private readonly WebUIBundleScanner? _bundleScanner;
    private readonly string _bundlesDirectory;

    public override string TabName => "Server";
    public override string TabIcon => "\uE1AA";

    // ── Server section ──
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _listenAddress = "127.0.0.1";
    [ObservableProperty] private int _listenPort = 8080;

    // ── Security section ──
    [ObservableProperty] private bool _enableHttps = true;
    [ObservableProperty] private string _httpsCertPath = "";
    [ObservableProperty] private string _httpsCertPassword = "";

    // ── Authentication section ──
    [ObservableProperty] private string _localUsername = "admin";
    [ObservableProperty] private int _jwtAccessTokenLifetimeMinutes = 15;
    [ObservableProperty] private int _jwtRefreshTokenLifetimeDays = 30;

    // ── OIDC section ──
    [ObservableProperty] private string _oidcAuthority = "";
    [ObservableProperty] private string _oidcClientId = "";
    [ObservableProperty] private string _oidcClientSecret = "";
    [ObservableProperty] private string _oidcAllowedEmail = "";

    // ── WebUI section ──
    [ObservableProperty] private string _selectedBundlePath = "";
    [ObservableProperty] private string _allowedOrigins = "*";

    // ── Startup section ──
    [ObservableProperty] private bool _openBrowserOnServerStart;

    // ── Password (write-only from dialog, stored as hash) ──
    [ObservableProperty] private string _localPasswordHash = "";

    // ── Status (read-only, bound from ServerHostService) ──
    [ObservableProperty] private ServerStatus _serverStatus = ServerStatus.Stopped;
    [ObservableProperty] private string? _listeningUrl;
    [ObservableProperty] private string? _serverErrorMessage;
    [ObservableProperty] private bool _isOidcExpanded;

    // ── Bundle dropdown ──
    public ObservableCollection<WebUIBundle> AvailableBundles { get; } = new();

    public ServerSettingsTabViewModel() : this(null, null, "") { }

    public ServerSettingsTabViewModel(
        ServerHostService? serverHost,
        WebUIBundleScanner? bundleScanner,
        string bundlesDirectory)
    {
        _serverHost = serverHost;
        _bundleScanner = bundleScanner;
        _bundlesDirectory = bundlesDirectory;

        if (_serverHost != null)
        {
            _serverHost.StatusChanged += OnServerStatusChanged;
            UpdateStatusFromHost();
        }

        RefreshBundles();
    }

    public override void LoadFromSettings(GlobalSettings settings)
    {
        var s = settings.Server;
        IsEnabled = s.Enabled;
        ListenAddress = s.ListenAddress;
        ListenPort = s.ListenPort;
        EnableHttps = s.EnableHttps;
        HttpsCertPath = s.HttpsCertPath;
        HttpsCertPassword = s.HttpsCertPassword;
        LocalUsername = s.LocalUsername;
        JwtAccessTokenLifetimeMinutes = s.JwtAccessTokenLifetimeMinutes;
        JwtRefreshTokenLifetimeDays = s.JwtRefreshTokenLifetimeDays;
        OidcAuthority = s.OidcAuthority;
        OidcClientId = s.OidcClientId;
        OidcClientSecret = s.OidcClientSecret;
        OidcAllowedEmail = s.OidcAllowedEmail;
        LocalPasswordHash = s.LocalPasswordHash;
        SelectedBundlePath = s.WebUIBundlePath;
        AllowedOrigins = s.AllowedOrigins;
        OpenBrowserOnServerStart = s.OpenBrowserOnServerStart;
    }

    public override void ApplyToSettings(GlobalSettings settings)
    {
        var s = settings.Server;
        s.Enabled = IsEnabled;
        s.ListenAddress = ListenAddress;
        s.ListenPort = ListenPort;
        s.EnableHttps = EnableHttps;
        s.HttpsCertPath = HttpsCertPath;
        s.HttpsCertPassword = HttpsCertPassword;
        s.LocalUsername = LocalUsername;
        s.JwtAccessTokenLifetimeMinutes = JwtAccessTokenLifetimeMinutes;
        s.JwtRefreshTokenLifetimeDays = JwtRefreshTokenLifetimeDays;
        s.OidcAuthority = OidcAuthority;
        s.OidcClientId = OidcClientId;
        s.OidcClientSecret = OidcClientSecret;
        s.OidcAllowedEmail = OidcAllowedEmail;
        s.LocalPasswordHash = LocalPasswordHash;
        s.WebUIBundlePath = SelectedBundlePath;
        s.AllowedOrigins = AllowedOrigins;
        s.OpenBrowserOnServerStart = OpenBrowserOnServerStart;
    }

    [RelayCommand]
    private void OpenWebUI()
    {
        _serverHost?.OpenBrowser();
    }

    [RelayCommand]
    private void ToggleOidc()
    {
        IsOidcExpanded = !IsOidcExpanded;
    }

    [RelayCommand]
    private void RefreshBundles()
    {
        AvailableBundles.Clear();

        if (_bundleScanner != null && !string.IsNullOrEmpty(_bundlesDirectory))
        {
            foreach (var bundle in _bundleScanner.ScanBundles(_bundlesDirectory))
                AvailableBundles.Add(bundle);
        }
        else
        {
            AvailableBundles.Add(new WebUIBundle("Default (built-in)", ""));
        }
    }

    private void OnServerStatusChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatusFromHost);
    }

    private void UpdateStatusFromHost()
    {
        if (_serverHost == null) return;
        ServerStatus = _serverHost.Status;
        ListeningUrl = _serverHost.ListeningUrl;
        ServerErrorMessage = _serverHost.ErrorMessage;
    }
}
