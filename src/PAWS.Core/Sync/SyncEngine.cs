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
    /// afterwards so the saved state reflects what is actually on disk/Drive now.
    /// </summary>
    public async Task<SyncResult> ApplyAsync(
        string accountId,
        SyncPlan plan,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await driveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var client = await clientFactory.CreateAsync(accountId, cancellationToken).ConfigureAwait(false);

            var executor = new SyncExecutor(client, throttle);
            var result = await executor.ExecuteAsync(
                plan.Pair.LocalPath, plan.RemoteRoot, plan.RemoteSnapshot, plan.Operations, progress, cancellationToken).ConfigureAwait(false);

            // Persist new last-known state from the now-current trees (best effort — don't fail the sync if
            // the re-capture hiccups; the next run will simply recompute).
            try
            {
                var remote = await new RemoteSnapshotBuilder(client).CaptureAsync(plan.Pair.RemotePath, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            driveGate.Release();
        }
    }
}
