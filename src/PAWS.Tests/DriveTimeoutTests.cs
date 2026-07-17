using PAWS.CloudFilter;
using PAWS.Core.Configuration;
using PAWS.Core.Sync;

namespace PAWS.Tests;

/// <summary>
/// Offline coverage (a fake IProtonDriveClientFactory that hangs forever on CreateAsync — no real Drive
/// account) for CloudSyncService's DriveTimeout event: when an on-demand Drive call is cancelled by its
/// bounded timeout (see EnableAsync/PopulateAsync), CreateFetchChildren's catch should raise DriveTimeout
/// with the right account/pair id before rethrowing. Ported from PAWS.AuthTest's --drivetimeouttest.
/// </summary>
[Collection("CloudFilter")]
public class DriveTimeoutTests
{
    [Fact]
    public async Task CancelledFetch_RaisesDriveTimeoutAndPropagatesCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "paws-drivetimeouttest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var engine = new CloudFilterPlaceholderEngine();
        var service = new CloudSyncService(
            engine, new FakeHangingClientFactory(), new FakeSyncStateStore(), new FakePopulatedFolderStore(), new SemaphoreSlim(1, 1));

        string? raisedAccountId = null;
        string? raisedPairId = null;
        var raiseCount = 0;
        service.DriveTimeout += (accountId, pairId) =>
        {
            raisedAccountId = accountId;
            raisedPairId = pairId;
            raiseCount++;
        };

        var pair = new SyncPair { Id = "pair-1", LocalPath = root, RemotePath = "/", Mode = SyncMode.OnDemand };

        try
        {
            // Self-cancels after 50ms — long enough for EnableAsync's own (uncontended, near-instant)
            // _enableGate.WaitAsync to complete first, so the cancellation lands inside the fetchChildren
            // call this test means to exercise rather than aborting before it's reached (an earlier
            // version pre-cancelled the token up front and never reached CreateFetchChildren at all).
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            // ThrowsAnyAsync (not ThrowsAsync) because Task.Delay(Infinite, ct) throws the derived
            // TaskCanceledException, not OperationCanceledException itself — the production code only
            // ever catches the base type, so a derived type here is correct, not a bug.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnableAsync("acct-1", pair, cts.Token));

            Assert.Equal(1, raiseCount);
            Assert.Equal("acct-1", raisedAccountId);
            Assert.Equal("pair-1", raisedPairId);
        }
        finally
        {
            try { engine.UnregisterSyncRoot(root); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
