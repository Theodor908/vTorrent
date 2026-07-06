using System.Threading.Tasks;
using FluentAssertions;
using vTorrent.Core.Orchestration;
using Xunit;

namespace vTorrent.Core.Tests.Orchestration;

public class CrashRecoveryProviderTests
{
    private const long StaleUnixSeconds = 1_000_000_000; // 2001 — far in the past

    private static ManagedTorrentResumeProvider ProviderWithLastSaved(long lastSaved)
    {
        var managed = new ManagedTorrent(new string('A', 40), "t");
        managed.ResumeData.LastSaved = lastSaved;
        return new ManagedTorrentResumeProvider(managed);
    }

    [Fact]
    public async Task StaleButSavedResume_DoesNotNeedCrashRecovery()
    {
        // Resume-data AGE must never force a re-verify (libtorrent alignment).
        (await ProviderWithLastSaved(StaleUnixSeconds).NeedsCrashRecoveryAsync())
            .Should().BeFalse();
    }

    [Fact]
    public async Task NeverSavedResume_NeedsCrashRecovery()
    {
        (await ProviderWithLastSaved(0).NeedsCrashRecoveryAsync())
            .Should().BeTrue();
    }
}
