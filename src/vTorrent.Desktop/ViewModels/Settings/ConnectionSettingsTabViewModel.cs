using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using vTorrent.Abstractions.Settings;

namespace vTorrent.Desktop.ViewModels.Settings;

/// <summary>
/// Connection tab: connection limits, listening port, port mapping, network interface.
/// </summary>
public partial class ConnectionSettingsTabViewModel : SettingsTabViewModelBase
{
    public override string TabName => "Connection";
    public override string TabIcon => "\uE408";

    [ObservableProperty]
    private int _maxGlobalConnections = 500;

    [ObservableProperty]
    private int _maxConnectionsPerTorrent = 50;

    [ObservableProperty]
    private int _maxUploadsPerTorrent = 4;

    [ObservableProperty]
    private int _listenPort = 6881;

    [ObservableProperty]
    private bool _portChangeRequiresRestart;

    [ObservableProperty]
    private bool _enablePortMapping = true;

    [ObservableProperty]
    private ObservableCollection<string> _networkInterfaces = new();

    [ObservableProperty]
    private string _selectedNetworkInterface = "All Interfaces (0.0.0.0)";

    public override void LoadFromSettings(GlobalSettings settings)
    {
        MaxGlobalConnections = settings.Connection.MaxGlobalConnections;
        MaxConnectionsPerTorrent = settings.Connection.MaxConnectionsPerTorrent;
        MaxUploadsPerTorrent = settings.Connection.MaxUploadsPerTorrent;
        ListenPort = settings.Connection.ListenPort;
        EnablePortMapping = settings.Connection.EnableUpnp;
        PortChangeRequiresRestart = false;

        PopulateNetworkInterfaces();
        var savedIp = settings.Connection.ListenInterfaces?.FirstOrDefault() ?? "0.0.0.0";
        if (savedIp == "0.0.0.0")
        {
            SelectedNetworkInterface = "All Interfaces (0.0.0.0)";
        }
        else
        {
            var ifaceMatch = NetworkInterfaces.FirstOrDefault(i => i.Contains($"({savedIp})"));
            if (ifaceMatch != null)
            {
                SelectedNetworkInterface = ifaceMatch;
            }
            // If saved interface not found, keep SelectedNetworkInterface as-is
            // (don't fall back to "All Interfaces" which would overwrite the saved value)
        }
    }

    public override void ApplyToSettings(GlobalSettings settings)
    {
        settings.Connection.MaxGlobalConnections = MaxGlobalConnections;
        settings.Connection.MaxConnectionsPerTorrent = MaxConnectionsPerTorrent;
        settings.Connection.MaxUploadsPerTorrent = MaxUploadsPerTorrent;
        settings.Connection.ListenPort = ListenPort;
        settings.Connection.EnableUpnp = EnablePortMapping;
        settings.Connection.EnableNatPmp = EnablePortMapping;

        if (SelectedNetworkInterface == "All Interfaces (0.0.0.0)" || string.IsNullOrEmpty(SelectedNetworkInterface))
        {
            settings.Connection.ListenInterfaces = new[] { "0.0.0.0", "[::]" };
        }
        else
        {
            var match = Regex.Match(SelectedNetworkInterface, @"\(([^)]+)\)");
            var ip = match.Success ? match.Groups[1].Value : "0.0.0.0";
            settings.Connection.ListenInterfaces = new[] { ip, "[::]" };
        }
    }

    /// <summary>
    /// Populate the network interfaces dropdown with available adapters.
    /// </summary>
    public void PopulateNetworkInterfaces()
    {
        NetworkInterfaces.Clear();
        NetworkInterfaces.Add("All Interfaces (0.0.0.0)");

        try
        {
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                var props = iface.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        NetworkInterfaces.Add($"{iface.Name} ({addr.Address})");
                        break;
                    }
                }
            }
        }
        catch
        {
            // If enumeration fails, "All Interfaces" is still available
        }
    }
}
