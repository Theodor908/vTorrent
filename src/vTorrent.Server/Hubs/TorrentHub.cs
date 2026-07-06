using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using vTorrent.Server.Services;

namespace vTorrent.Server.Hubs;

[Authorize]
public class TorrentHub : Hub
{
    private readonly TorrentHubRelay _relay;

    public TorrentHub(TorrentHubRelay relay)
    {
        _relay = relay;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Ping — verifies auth works.</summary>
    public string Ping() => "pong";

    /// <summary>Subscribe to detailed updates for a specific torrent.</summary>
    public async Task SubscribeTorrent(string infoHash)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"torrent:{infoHash}");
        _relay.TrackSubscription(infoHash);
    }

    /// <summary>Unsubscribe from detailed updates for a specific torrent.</summary>
    public async Task UnsubscribeTorrent(string infoHash)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"torrent:{infoHash}");
        _relay.UntrackSubscription(infoHash);
    }
}
