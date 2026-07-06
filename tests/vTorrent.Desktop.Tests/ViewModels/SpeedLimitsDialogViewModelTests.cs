using FluentAssertions;
using vTorrent.Desktop.ViewModels.Dialogs;
using Xunit;

namespace vTorrent.Tests.Unit.ViewModels;

public class SpeedLimitsDialogViewModelTests
{
    [Fact]
    public void Constructor_ShouldInitializeWithDefaults()
    {
        var vm = new SpeedLimitsDialogViewModel();
        vm.DownloadLimitDisplay.Should().Be(0);
        vm.UploadLimitDisplay.Should().Be(0);
        vm.SelectedUnit.Should().Be("KB/s");
        vm.UnitOptions.Should().Contain("KB/s");
        vm.UnitOptions.Should().Contain("MB/s");
        vm.UnitOptions.Should().Contain("GB/s");
    }

    [Fact]
    public void IsUnlimitedDownload_WhenZero_ShouldBeTrue()
    {
        var vm = new SpeedLimitsDialogViewModel();
        vm.DownloadLimitDisplay = 0;
        vm.IsUnlimitedDownload.Should().BeTrue();
    }

    [Fact]
    public void IsUnlimitedDownload_WhenNonZero_ShouldBeFalse()
    {
        var vm = new SpeedLimitsDialogViewModel();
        vm.DownloadLimitDisplay = 100;
        vm.IsUnlimitedDownload.Should().BeFalse();
    }

    [Fact]
    public void IsUnlimitedUpload_WhenZero_ShouldBeTrue()
    {
        var vm = new SpeedLimitsDialogViewModel();
        vm.UploadLimitDisplay = 0;
        vm.IsUnlimitedUpload.Should().BeTrue();
    }

    [Fact]
    public void GetDownloadLimitBytes_WithKBUnit_ShouldConvert()
    {
        var vm = new SpeedLimitsDialogViewModel();
        vm.SelectedUnit = "KB/s";
        vm.DownloadLimitDisplay = 500;
        vm.GetDownloadLimitBytes().Should().Be(500 * 1024);
    }

    [Fact]
    public void GetUploadLimitBytes_WithMBUnit_ShouldConvert()
    {
        var vm = new SpeedLimitsDialogViewModel();
        vm.SelectedUnit = "MB/s";
        vm.UploadLimitDisplay = 5;
        vm.GetUploadLimitBytes().Should().Be(5 * 1024 * 1024);
    }

    [Fact]
    public void GetDownloadLimitBytes_WhenZero_ShouldReturnZero()
    {
        var vm = new SpeedLimitsDialogViewModel();
        vm.DownloadLimitDisplay = 0;
        vm.GetDownloadLimitBytes().Should().Be(0);
    }

    [Fact]
    public void LoadFromBytes_ShouldAutoDetectUnit()
    {
        var vm = new SpeedLimitsDialogViewModel();
        vm.LoadFromBytes(5 * 1024 * 1024, 10 * 1024 * 1024);
        vm.SelectedUnit.Should().Be("MB/s");
        vm.DownloadLimitDisplay.Should().Be(5);
        vm.UploadLimitDisplay.Should().Be(10);
    }
}
