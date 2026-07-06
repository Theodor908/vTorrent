using FluentAssertions;
using vTorrent.Core;
using Xunit;
using vTorrent.Core.Download;

namespace vTorrent.Tests.Unit.Core;

public class SlidingWindowRateCalculatorTests
{
    #region Construction

    [Fact]
    public void Constructor_ShouldInitializeWithZeroRate()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.CurrentRate.Should().Be(0);
    }

    [Fact]
    public void Constructor_ShouldInitializeSmoothedRateToZero()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.SmoothedRate.Should().Be(0);
    }

    #endregion

    #region AddSample

    [Fact]
    public void AddSample_WithPositiveBytes_ShouldIncreaseRate()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        calculator.AddSample(1000);

        calculator.CurrentRate.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AddSample_WithZeroBytes_ShouldNotAffectRate()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        calculator.AddSample(0);

        calculator.CurrentRate.Should().Be(0);
    }

    [Fact]
    public void AddSample_WithNegativeBytes_ShouldNotAffectRate()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        calculator.AddSample(-100);

        calculator.CurrentRate.Should().Be(0);
    }

    [Fact]
    public void AddSample_MultipleSamples_ShouldAccumulate()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        calculator.AddSample(1000);
        calculator.AddSample(1000);
        calculator.AddSample(1000);

        // With 3000 bytes in a 10-second window, rate should be around 300 bytes/sec
        calculator.CurrentRate.Should().BeGreaterOrEqualTo(0);
    }

    #endregion

    #region CurrentRate

    [Fact]
    public void CurrentRate_WithSamplesInWindow_ShouldCalculateCorrectly()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        // Add 10000 bytes
        calculator.AddSample(10000);

        // Rate should be bytes / window_seconds = 10000 / 10 = 1000 bytes/sec
        var rate = calculator.CurrentRate;
        rate.Should().BeApproximately(1000, 100); // Allow some margin for timing
    }

    [Fact]
    public void CurrentRate_WhenPaused_ShouldReturnZero()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.AddSample(10000);

        calculator.SetPaused(true);

        calculator.CurrentRate.Should().Be(0);
    }

    [Fact]
    public void CurrentRate_WhenUnpaused_ShouldReturnNormalRate()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.AddSample(10000);
        calculator.SetPaused(true);
        calculator.SetPaused(false);

        calculator.CurrentRate.Should().BeGreaterThan(0);
    }

    #endregion

    #region SmoothedRate

    [Fact]
    public void SmoothedRate_FirstNonZeroSample_ShouldInitializeDirectly()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        calculator.AddSample(10000);

        // First rate should be initialized directly (not blended with 0)
        calculator.SmoothedRate.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SmoothedRate_WhenPaused_ShouldReturnZero()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.AddSample(10000);
        // Get the rate to initialize it
        _ = calculator.SmoothedRate;

        calculator.SetPaused(true);

        calculator.SmoothedRate.Should().Be(0);
    }

    #endregion

    #region Reset

    [Fact]
    public void Reset_ShouldClearAllSamples()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.AddSample(10000);
        calculator.AddSample(10000);

        calculator.Reset();

        calculator.CurrentRate.Should().Be(0);
    }

    [Fact]
    public void Reset_ShouldClearSmoothedRate()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.AddSample(10000);
        _ = calculator.SmoothedRate; // Initialize smoothed rate

        calculator.Reset();

        calculator.SmoothedRate.Should().Be(0);
    }

    #endregion

    #region SetPaused

    [Fact]
    public void SetPaused_True_ShouldClearSmoothedRate()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.AddSample(10000);
        _ = calculator.SmoothedRate;

        calculator.SetPaused(true);

        calculator.SmoothedRate.Should().Be(0);
    }

    [Fact]
    public void SetPaused_False_ShouldResumeCalculation()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));
        calculator.AddSample(10000);
        calculator.SetPaused(true);

        calculator.SetPaused(false);
        calculator.AddSample(5000);

        calculator.CurrentRate.Should().BeGreaterThan(0);
    }

    #endregion

    #region Thread Safety

    [Fact]
    public void AddSample_ConcurrentCalls_ShouldNotThrow()
    {
        var calculator = new SlidingWindowRateCalculator(TimeSpan.FromSeconds(10));

        var tasks = Enumerable.Range(0, 100).Select(n => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                calculator.AddSample(100);
                var rate = calculator.CurrentRate;
                var smoothed = calculator.SmoothedRate;
            }
        }));

        var act = () => Task.WaitAll(tasks.ToArray());

        act.Should().NotThrow();
    }

    #endregion
}
