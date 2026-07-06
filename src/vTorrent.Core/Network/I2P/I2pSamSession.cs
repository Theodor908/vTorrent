// src/vTorrent.Core/Network/I2P/I2pSamSession.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Models;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Core.Network.I2P;

/// <summary>
/// Manages the SAM session lifecycle: destination generation/persistence/rotation,
/// session creation, and connection state.
/// </summary>
public sealed class I2pSamSession
{
    private readonly I2pSettings _settings;
    private readonly string _dataDirectory;
    private readonly string _keyFilePath;

    private I2pSamClient? _controlClient;
    private string? _privateKey;
    private string? _publicKey;
    private DateTime _keyCreatedAt;

    public string? SessionId { get; private set; }
    public I2pDestination? LocalDestination { get; private set; }
    public bool IsConnected { get; private set; }
    public string SamHostname => _settings.SamHostname;
    public int SamPort => _settings.SamPort;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler? DestinationRotated;

    public I2pSamSession(I2pSettings settings, string dataDirectory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _keyFilePath = Path.Combine(_dataDirectory, "i2p_destination.key");
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Clean up any existing session before creating a new one
        if (_controlClient != null)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }

        _controlClient = new I2pSamClient(_settings.SamHostname, _settings.SamPort);
        await _controlClient.HandshakeAsync(ct).ConfigureAwait(false);

        // Resolve destination based on mode
        var destination = await ResolveDestinationAsync(ct).ConfigureAwait(false);

        // Use a stable session ID so we can reclaim it across reconnects
        SessionId = "vtorrent_session";
        var tunnels = I2pTunnelConfig.FromSettings(_settings);
        await _controlClient.CreateSessionAsync(SessionId, destination, tunnels, ct)
            .ConfigureAwait(false);

        // Build local destination from public key if available
        if (_publicKey != null)
        {
            LocalDestination = I2pDestination.FromBase64(_publicKey);
        }

        IsConnected = true;
        Connected?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        if (_controlClient != null)
        {
            await _controlClient.DisposeAsync().ConfigureAwait(false);
            _controlClient = null;
        }

        IsConnected = false;
        SessionId = null;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> ResolveDestinationAsync(CancellationToken ct)
    {
        switch (_settings.DestinationMode)
        {
            case I2pDestinationMode.SessionTransient:
                // Generate a transient destination (not saved)
                var (pub, _) = await _controlClient!.GenerateDestinationAsync(ct: ct).ConfigureAwait(false);
                _publicKey = pub;
                return "TRANSIENT";

            case I2pDestinationMode.Persistent:
                return await LoadOrGenerateKeyAsync(ct).ConfigureAwait(false);

            case I2pDestinationMode.Rotating:
                return await LoadOrGenerateKeyAsync(ct).ConfigureAwait(false);

            default:
                throw new ArgumentOutOfRangeException(nameof(_settings.DestinationMode));
        }
    }

    private async Task<string> LoadOrGenerateKeyAsync(CancellationToken ct)
    {
        // Try to load existing key
        if (File.Exists(_keyFilePath))
        {
            var savedData = await File.ReadAllTextAsync(_keyFilePath, ct).ConfigureAwait(false);
            var parts = savedData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                _publicKey = parts[0];
                _privateKey = parts[1];
                _keyCreatedAt = File.GetCreationTimeUtc(_keyFilePath);

                // Check rotation for Rotating mode
                if (_settings.DestinationMode == I2pDestinationMode.Rotating)
                {
                    var age = DateTime.UtcNow - _keyCreatedAt;
                    if (age.TotalDays >= _settings.RotationIntervalDays)
                    {
                        return await GenerateAndSaveKeyAsync(ct).ConfigureAwait(false);
                    }
                }

                return _privateKey;
            }
        }

        // Generate new key
        return await GenerateAndSaveKeyAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> GenerateAndSaveKeyAsync(CancellationToken ct)
    {
        var (pub, priv) = await _controlClient!.GenerateDestinationAsync(ct: ct).ConfigureAwait(false);
        _publicKey = pub;
        _privateKey = priv;
        _keyCreatedAt = DateTime.UtcNow;

        // Persist to file
        Directory.CreateDirectory(_dataDirectory);
        await File.WriteAllTextAsync(_keyFilePath, $"{pub}\n{priv}", ct).ConfigureAwait(false);

        return priv;
    }

    public async Task RotateDestinationAsync(CancellationToken ct = default)
    {
        // Disconnect current session
        await DisconnectAsync().ConfigureAwait(false);

        // Delete old key
        if (File.Exists(_keyFilePath))
            File.Delete(_keyFilePath);

        // Reconnect with new destination
        await ConnectAsync(ct).ConfigureAwait(false);

        DestinationRotated?.Invoke(this, EventArgs.Empty);
    }
}
