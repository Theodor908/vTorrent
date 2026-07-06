using vTorrent.Abstractions.Settings;
using vTorrent.Abstractions.Settings.Enums;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class SettingsResolverTests
{
    [Fact]
    public void Resolve_NullableEnum_ReturnsPerTorrent_WhenSet()
    {
        ChokingAlgorithm? perTorrent = ChokingAlgorithm.Adaptive;
        var result = SettingsResolver.Resolve(perTorrent, ChokingAlgorithm.RateBased);
        Assert.Equal(ChokingAlgorithm.Adaptive, result);
    }

    [Fact]
    public void Resolve_NullableEnum_ReturnsGlobal_WhenNull()
    {
        ChokingAlgorithm? perTorrent = null;
        var result = SettingsResolver.Resolve(perTorrent, ChokingAlgorithm.RateBased);
        Assert.Equal(ChokingAlgorithm.RateBased, result);
    }

    [Fact]
    public void Resolve_Int_ReturnsPerTorrent_WhenNotSentinel()
    {
        var result = SettingsResolver.Resolve(42, 100);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Resolve_Int_ReturnsGlobal_WhenSentinel()
    {
        var result = SettingsResolver.Resolve(-1, 100);
        Assert.Equal(100, result);
    }

    [Fact]
    public void Resolve_NullableBool_ReturnsPerTorrent_WhenSet()
    {
        bool? perTorrent = true;
        var result = SettingsResolver.Resolve(perTorrent, false);
        Assert.True(result);
    }

    [Fact]
    public void Resolve_NullableBool_ReturnsGlobal_WhenNull()
    {
        bool? perTorrent = null;
        var result = SettingsResolver.Resolve(perTorrent, false);
        Assert.False(result);
    }
}
