using vTorrent.Desktop.Services;
using vTorrent.Desktop.ViewModels;
using Xunit;

namespace vTorrent.Tests.Unit.Desktop.State;

/// <summary>
/// The debouncer suppresses transitional states (Connecting, Verifying, ...) that flash by
/// during a fast engine startup, while still surfacing a transitional state that genuinely
/// persists (e.g. a long recheck). Stable/destination states commit instantly.
///
/// Resolve() takes an explicit nowMs so the temporal behaviour is fully deterministic.
/// </summary>
public class DisplayStateDebouncerTests
{
    private const long Threshold = 400;

    private static DisplayStateDebouncer New() => new(debounceMs: Threshold);

    [Fact]
    public void FirstObservation_CommitsImmediately()
    {
        // Nothing prior to show — whatever the first snapshot says, show it.
        var d = New();
        Assert.Equal(TorrentDisplayState.Queued, d.Resolve(TorrentDisplayState.Queued, nowMs: 0));
    }

    [Fact]
    public void StableState_CommitsImmediately()
    {
        var d = New();
        d.Resolve(TorrentDisplayState.Queued, nowMs: 0);
        // Downloading is a destination — never debounced.
        Assert.Equal(TorrentDisplayState.Downloading, d.Resolve(TorrentDisplayState.Downloading, nowMs: 10));
    }

    [Fact]
    public void FastStartup_TransientFlashesSuppressed_OnlyFinalShown()
    {
        // Realistic fast path: Queued -> Connecting -> Verifying -> Downloading within ~50ms.
        var d = New();
        Assert.Equal(TorrentDisplayState.Queued, d.Resolve(TorrentDisplayState.Queued, nowMs: 0));

        // Transitional states arriving faster than the threshold must NOT be shown.
        Assert.Equal(TorrentDisplayState.Queued, d.Resolve(TorrentDisplayState.Connecting, nowMs: 5));
        Assert.Equal(TorrentDisplayState.Queued, d.Resolve(TorrentDisplayState.Verifying, nowMs: 20));

        // Final destination arrives before threshold -> jump straight to it.
        Assert.Equal(TorrentDisplayState.Downloading, d.Resolve(TorrentDisplayState.Downloading, nowMs: 50));
    }

    [Fact]
    public void PersistentTransient_ShownAfterThreshold()
    {
        // A long recheck: Verifying sticks around well past the threshold.
        var d = New();
        d.Resolve(TorrentDisplayState.Queued, nowMs: 0);

        Assert.Equal(TorrentDisplayState.Queued, d.Resolve(TorrentDisplayState.Verifying, nowMs: 0));   // pending
        Assert.Equal(TorrentDisplayState.Queued, d.Resolve(TorrentDisplayState.Verifying, nowMs: 200)); // still within window
        // Crossed the threshold while staying Verifying -> commit it.
        Assert.Equal(TorrentDisplayState.Verifying, d.Resolve(TorrentDisplayState.Verifying, nowMs: 450));
    }

    [Fact]
    public void DifferentTransient_ResetsTheWindow()
    {
        var d = New();
        d.Resolve(TorrentDisplayState.Downloading, nowMs: 0);

        // Connecting pends at t=0...
        Assert.Equal(TorrentDisplayState.Downloading, d.Resolve(TorrentDisplayState.Connecting, nowMs: 0));
        // ...then a DIFFERENT transient at t=300 restarts the clock, so even at t=350
        // (350ms after the first transient, but only 50ms after this one) nothing commits.
        Assert.Equal(TorrentDisplayState.Downloading, d.Resolve(TorrentDisplayState.Verifying, nowMs: 300));
        Assert.Equal(TorrentDisplayState.Downloading, d.Resolve(TorrentDisplayState.Verifying, nowMs: 350));
        // Verifying finally persists past its own window.
        Assert.Equal(TorrentDisplayState.Verifying, d.Resolve(TorrentDisplayState.Verifying, nowMs: 720));
    }

    [Fact]
    public void ReturningToCommitted_CancelsPending()
    {
        var d = New();
        d.Resolve(TorrentDisplayState.Downloading, nowMs: 0);

        // A blip of Connecting pends but never commits...
        Assert.Equal(TorrentDisplayState.Downloading, d.Resolve(TorrentDisplayState.Connecting, nowMs: 10));
        // ...and we snap back to the already-committed Downloading, clearing the pending blip.
        Assert.Equal(TorrentDisplayState.Downloading, d.Resolve(TorrentDisplayState.Downloading, nowMs: 20));
        // The earlier Connecting must not resurrect now that its timer would have elapsed.
        Assert.Equal(TorrentDisplayState.Downloading, d.Resolve(TorrentDisplayState.Downloading, nowMs: 1000));
    }
}
