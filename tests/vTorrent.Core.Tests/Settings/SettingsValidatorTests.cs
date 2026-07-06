using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Abstractions.Settings;
using Xunit;

namespace vTorrent.Core.Tests.Settings;

public class SettingsValidatorTests
{
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public void Clamp_InRange_ReturnsOriginal()
        => Assert.Equal(50, SettingsValidator.Clamp(50, 0, 100, "test", _logger));

    [Fact]
    public void Clamp_BelowMin_ReturnsMin()
        => Assert.Equal(0, SettingsValidator.Clamp(-5, 0, 100, "test", _logger));

    [Fact]
    public void Clamp_AboveMax_ReturnsMax()
        => Assert.Equal(100, SettingsValidator.Clamp(150, 0, 100, "test", _logger));

    [Fact]
    public void Clamp_AtBoundary_ReturnsOriginal()
    {
        Assert.Equal(0, SettingsValidator.Clamp(0, 0, 100, "test", _logger));
        Assert.Equal(100, SettingsValidator.Clamp(100, 0, 100, "test", _logger));
    }

    [Fact]
    public void ClampDouble_InRange_ReturnsOriginal()
        => Assert.Equal(0.5, SettingsValidator.Clamp(0.5, 0.1, 1.0, "test", _logger));

    [Fact]
    public void ClampDouble_OutOfRange_Clamps()
        => Assert.Equal(0.1, SettingsValidator.Clamp(0.0, 0.1, 1.0, "test", _logger));
}
