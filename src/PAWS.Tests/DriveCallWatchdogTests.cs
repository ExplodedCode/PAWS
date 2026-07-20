using System.Diagnostics;
using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Coverage for CloudSyncService's drive-call watchdog: a metadata call that never returns AND ignores
/// its CancellationToken must not be able to hang the shared client gate forever. Regression for the
/// live 2026-07-17 incident — a stuck Drive call left every subsequent listing waiting out its own
/// 60-second budget every ~25-35s (Explorer's retry cadence) for minutes, with a bare
/// "OperationCanceledException: The operation was canceled" and no indication that retrying was
/// pointless, while an out-of-process probe against the same account/session/folder succeeded in under a
/// second — proving the account and Drive session were fine and the already-running process's own gate
/// was the actual problem.
/// </summary>
public class DriveCallWatchdogTests
{
    private static CloudSyncService MakeService(TimeSpan watchdogTimeout) => new(
        new ThrowingPlaceholderEngine(),
        new FakeStuckClientFactory(),
        new FakeSyncStateStore(),
        new FakePopulatedFolderStore(),
        new SemaphoreSlim(1, 1),
        throttle: null,
        driveMetadataCallTimeout: watchdogTimeout);

    [Fact]
    public async Task StuckCall_TripsWatchdogQuickly_RaisesDriveSessionWedgedOnce_AndPoisonsTheGate()
    {
        var service = MakeService(TimeSpan.FromMilliseconds(200));

        string? wedgedAccountId = null;
        string? wedgedPairId = null;
        var wedgedCount = 0;
        service.DriveSessionWedged += (accountId, pairId) =>
        {
            wedgedAccountId = accountId;
            wedgedPairId = pairId;
            wedgedCount++;
        };

        // InvalidOperationException (not a cancellation): a wedge is a hard "restart PAWS" condition —
        // a cancellation type here would be translated upstream into the softer, misleading "couldn't
        // reach Proton Drive — wait and retry" DriveTimeout signal.
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetWebUrlAsync("acct-1", "/some/path", "pair-1"));
        sw.Stop();
        Assert.Contains("quit and reopen PAWS", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Proves the watchdog actually bounded it — nowhere near a real 60s default, and comfortably
        // above the 200ms budget so this isn't just measuring scheduler noise.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"watchdog should fire quickly, took {sw.Elapsed}");

        Assert.Equal(1, wedgedCount);
        Assert.Equal("acct-1", wedgedAccountId);
        Assert.Equal("pair-1", wedgedPairId);

        // A second call — even for a different pair/path — must fail FAST because the gate is now
        // permanently poisoned, not wait out another full watchdog budget only to discover the same thing.
        var sw2 = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetWebUrlAsync("acct-1", "/some/other/path", "pair-2"));
        sw2.Stop();

        Assert.True(sw2.Elapsed < TimeSpan.FromMilliseconds(150), $"second call should fail fast once poisoned, took {sw2.Elapsed}");

        // DriveSessionWedged is a one-time signal, not one per subsequent fail-fast call.
        Assert.Equal(1, wedgedCount);
    }

    [Fact]
    public async Task HealthyCall_NeverTripsTheWatchdog()
    {
        // A sanity check in the other direction: GetWebUrlAsync against a plain-throwing (not stuck)
        // client must fail with the client's own exception, not a watchdog timeout, and DriveSessionWedged
        // must never fire — proves the watchdog only reacts to a genuine hang, not every failure.
        var service = new CloudSyncService(
            new ThrowingPlaceholderEngine(),
            new ImmediateFailureClientFactory(),
            new FakeSyncStateStore(),
            new FakePopulatedFolderStore(),
            new SemaphoreSlim(1, 1),
            throttle: null,
            driveMetadataCallTimeout: TimeSpan.FromSeconds(30));

        var wedgedCount = 0;
        service.DriveSessionWedged += (_, _) => wedgedCount++;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetWebUrlAsync("acct-1", "/some/path", "pair-1"));

        Assert.Equal(0, wedgedCount);
    }
}
