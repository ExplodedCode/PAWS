using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Drive;

namespace PAWS.Core.Sync;

/// <summary>
/// Orchestrates a full sync for one pair, composing the building blocks: connect (resume session),
/// capture the remote + local trees, load last-known state, reconcile into a plan, apply it, then
/// re-capture and persist the new state. Split into <see cref="PlanAsync"/> and <see cref="ApplyAsync"/>
/// so callers can preview the plan and require confirmation before any file moves.
/// <para>Every Drive-touching operation is serialized on the shared <paramref name="driveGate"/>: the
/// native crypto (proton_crypto.dll) is not concurrency-safe, so full-sync work must never run at the same
/// time as on-demand hydration/push/pull (which share the same gate).</para>
/// </summary>
public sealed class SyncEngine(
    IProtonDriveClientFactory clientFactory, ISyncStateStore stateStore, SemaphoreSlim driveGate, TransferThrottle? throttle = null)
{
    /// <summary>Captures both trees and reconciles them against last-known state — no files are moved.</summary>
    public async Task<SyncPlan> PlanAsync(string accountId, SyncPair pair, CancellationToken cancellationToken = default)
    {
        await driveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var client = await clientFactory.CreateAsync(accountId, cancellationToken).ConfigureAwait(false);

            var root = await client.ResolvePathAsync(pair.RemotePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Remote folder not found: {pair.RemotePath}");

            var remote = await new RemoteSnapshotBuilder(client).CaptureAsync(pair.RemotePath, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Remote path is not a folder: {pair.RemotePath}");

            // Full-sync mirrors the whole tree (no lazy population), so walk everything (null scope).
            var local = new LocalSnapshotBuilder().Capture(pair.LocalPath, null, cancellationToken)
                ?? throw new InvalidOperationException($"Local folder not found: {pair.LocalPath}");

            var lastKnown = stateStore.Load(pair.Id) ?? SyncState.Empty(pair.Id);
            var operations = new Reconciler().Reconcile(remote, local, lastKnown);

            return new SyncPlan(pair, remote, local, root, operations);
        }
        finally
        {
            driveGate.Release();
        }
    }

    /// <summary>
    /// Applies a previously-computed plan and persists the resulting state. Re-captures both trees
    /// afterwards so the saved state reflects what is actually on disk/Drive now. Conflicted paths are
    /// skipped unless <paramref name="conflictResolutions"/> carries the user's decision for them.
    /// </summary>
    public async Task<SyncResult> ApplyAsync(
        string accountId,
        SyncPlan plan,
        IProgress<SyncProgress>? progress = null,
        IReadOnlyDictionary<string, ConflictResolution>? conflictResolutions = null,
        CancellationToken cancellationToken = default)
    {
        IProtonDriveClient client = null!;
        await GateAsync(async ct => client = await clientFactory.CreateAsync(accountId, ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var executor = new SyncExecutor(client, throttle, pairUploadLimitKBps: plan.Pair.UploadLimitKBps, pairDownloadLimitKBps: plan.Pair.DownloadLimitKBps);

            // Gated PER OPERATION (see SyncExecutor's remarks on `gate`) rather than one lock held across
            // the whole apply: a large/slow sync (many files, or one big one under a low speed limit)
            // must not starve pending Explorer-triggered hydration/browse callbacks on any on-demand pair
            // sharing this same drive gate for its entire duration — that starvation is what previously
            // froze Explorer and produced "the cloud operation was not completed before the time-out
            // period expired". Releasing the lock between files lets those requests through.
            var result = await executor.ExecuteAsync(
                plan.Pair.LocalPath, plan.RemoteRoot, plan.RemoteSnapshot, plan.Operations,
                progress: progress, conflictResolutions: conflictResolutions, gate: GateAsync, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Persist new last-known state from the now-current trees (best effort — don't fail the sync if
            // the re-capture hiccups; the next run will simply recompute).
            try
            {
                RemoteSnapshot? remote = null;
                await GateAsync(async ct =>
                {
                    remote = await new RemoteSnapshotBuilder(client).CaptureAsync(plan.Pair.RemotePath, cancellationToken: ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);

                var local = new LocalSnapshotBuilder().Capture(plan.Pair.LocalPath, null, cancellationToken);
                if (remote is not null && local is not null)
                {
                    stateStore.Save(SyncStateBuilder.Build(plan.Pair.Id, remote, local));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Leave state as-is; reconcile is self-correcting next run.
            }

            return result;
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Acquires the shared crypto/SDK gate for exactly the duration of one inner action — used both
    // directly (client creation, the final re-capture) and as the `gate` handed to SyncExecutor (for
    // each individual upload/download/create/trash) — rather than one lock held across the whole apply.
    // See SyncExecutor's remarks for why a continuously-held lock is a problem.
    private async Task GateAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await driveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            driveGate.Release();
        }
    }
}
