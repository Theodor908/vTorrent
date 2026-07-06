using vTorrent.Abstractions.Settings;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class SettingsMonitorTests
{
    [Fact]
    public void CurrentValue_ReturnsLatestUpdate()
    {
        var monitor = new SettingsMonitor<BehaviorSettings>();
        var settings = new BehaviorSettings { PeerTurnover = 10 };
        monitor.Update(settings);
        Assert.Equal(10, monitor.CurrentValue.PeerTurnover);
    }

    [Fact]
    public void OnChange_FiresOnUpdate()
    {
        var monitor = new SettingsMonitor<BehaviorSettings>();
        BehaviorSettings? received = null;
        monitor.OnChange((s, _) => received = s);

        var settings = new BehaviorSettings { PeerTurnover = 20 };
        monitor.Update(settings);

        Assert.NotNull(received);
        Assert.Equal(20, received!.PeerTurnover);
    }

    [Fact]
    public void OnChange_Dispose_StopsNotifications()
    {
        var monitor = new SettingsMonitor<BehaviorSettings>();
        int callCount = 0;
        var registration = monitor.OnChange((_, _) => callCount++);

        monitor.Update(new BehaviorSettings());
        Assert.Equal(1, callCount);

        registration!.Dispose();
        monitor.Update(new BehaviorSettings());
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void OnChange_MultipleSubscribers_AllNotified()
    {
        var monitor = new SettingsMonitor<BehaviorSettings>();
        int count1 = 0, count2 = 0;
        monitor.OnChange((_, _) => count1++);
        monitor.OnChange((_, _) => count2++);

        monitor.Update(new BehaviorSettings());
        Assert.Equal(1, count1);
        Assert.Equal(1, count2);
    }
}
