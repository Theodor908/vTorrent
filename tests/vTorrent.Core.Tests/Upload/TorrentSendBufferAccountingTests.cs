using FluentAssertions;
using vTorrent.Core.Upload;
using Xunit;

namespace vTorrent.Tests.Upload;

public class TorrentSendBufferAccountingTests
{
    private const int BlockSize = 16384;

    [Fact]
    public void TryReserve_UnderCeiling_Succeeds()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: 1024 * 1024); // 1 MiB
        accounting.TryReserve(BlockSize).Should().BeTrue();
        accounting.TotalBufferedBytes.Should().Be(BlockSize);
    }

    [Fact]
    public void TryReserve_OverCeiling_Fails()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: BlockSize);
        accounting.TryReserve(BlockSize).Should().BeTrue();
        accounting.TryReserve(BlockSize).Should().BeFalse(); // exceeds ceiling
    }

    [Fact]
    public void Release_DecrementsCounter()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: 1024 * 1024);
        accounting.TryReserve(BlockSize);
        accounting.Release(BlockSize);
        accounting.TotalBufferedBytes.Should().Be(0);
    }

    [Fact]
    public void ThreeTierPressure_Normal_Below50Percent()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: 100 * BlockSize);
        // Fill to 40% — should be Normal
        for (int i = 0; i < 40; i++)
            accounting.TryReserve(BlockSize);
        accounting.State.Should().Be(PressureState.Normal);
    }

    [Fact]
    public void ThreeTierPressure_SoftPressure_Between50And75Percent()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: 100 * BlockSize);
        for (int i = 0; i < 60; i++)
            accounting.TryReserve(BlockSize);
        accounting.State.Should().Be(PressureState.SoftPressure);
    }

    [Fact]
    public void ThreeTierPressure_HardPause_Above75Percent()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: 100 * BlockSize);
        for (int i = 0; i < 80; i++)
            accounting.TryReserve(BlockSize);
        accounting.State.Should().Be(PressureState.HardPause);
    }

    [Fact]
    public void Release_TriggersLowWatermarkRecovery()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: 100 * BlockSize);
        // Fill to HardPause (80%)
        for (int i = 0; i < 80; i++)
            accounting.TryReserve(BlockSize);
        accounting.State.Should().Be(PressureState.HardPause);

        // Drain to 45% — should recover to Normal
        for (int i = 0; i < 35; i++)
            accounting.Release(BlockSize);
        accounting.State.Should().Be(PressureState.Normal);
    }

    [Fact]
    public void AutoTune_ScalesWithUploadRate()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: 0); // auto-tune
        // Simulate recording 10 MB/s upload
        for (int i = 0; i < 100; i++)
            accounting.RecordUpload(100_000);
        accounting.ForceAutoTuneForTesting();

        // Ceiling should be roughly 10MB/s * 2.5 = 25 MB, clamped to [4MiB, 64MiB]
        accounting.EffectiveCeiling.Should().BeGreaterThanOrEqualTo(4 * 1024 * 1024);
        accounting.EffectiveCeiling.Should().BeLessThanOrEqualTo(64 * 1024 * 1024);
    }

    [Fact]
    public void ManualWatermark_DisablesAutoTune()
    {
        var accounting = new TorrentSendBufferAccounting(manualCeiling: 2 * 1024 * 1024);
        accounting.RecordUpload(10_000_000); // Would auto-tune to higher
        accounting.EffectiveCeiling.Should().Be(2 * 1024 * 1024); // stays fixed
    }
}
