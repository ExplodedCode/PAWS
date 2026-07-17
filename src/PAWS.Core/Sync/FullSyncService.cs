using PAWS.Core.Configuration;

namespace PAWS.Core.Sync;

/// <summary>
/// Automatic two-way sync for <see cref="SyncMode.FullSync"/> pairs (real local files, not placeholders).
/// For each enabled pair it watches the local folder (debounced) AND polls Drive periodically, running the
/// full <see cref="SyncEngine"/> plan+apply on each trigger so local and remote stay mirrored both ways.
/// <para><b>Delete safety:</b> unlike the manual "Sync now" (which shows the plan and confirms before
/// moving anything), auto-sync applies without a dialog — so a plan that would delete more than
/// <see cref="MaxAutoDeletes"/> items is NOT applied automatically. Instead it's flagged (<see
/// cref="FullSyncEventArgs.NeedsReview"/>) for the user to apply via manual "Sync now". This guards against
/// a transient glitch (e.g. the local folder briefly emptied/unmounted) auto-propagating a mass deletion.</para>
/// <para>Crypto serialization is handled by the <see cref="SyncEngine"/>'s shared drive gate, so full-sync
/// work never runs concurrently with on-demand hydration/push/pull.</para>
/// </summary>
public sealed class FullSyncService(SyncEngine syncEngine) : IDisposable
{
    // A plan deleting more than this many items is held for manual review instead of auto-applied.
    private const int MaxAutoDeletes = 50;

    // A flat count alone lets a small-folder "wipe" through un-reviewed too easily — e.g. 6 of 8 files
    // vanishing is only 6 deletes (nowhere near MaxAutoDeletes) but is proportionally catastrophic. The
    // proportion check is skipped below MinTreeSizeForProportionGuard so an ordinary one- or two-file
    // deletion in a tiny folder doesn't demand manual review just because it happens to be "most" of it.
    private const double ProportionAutoDeleteThreshold = 0.5;
    private const int MinTreeSizeForProportionGuard = 4;

    // The watcher catches local edits; this poll catches remote-side changes (SyncEngine resumes a fresh
    // client per plan, so it sees current Drive state — no stale-cache problem).
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollStartDelay = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, FolderWatcher> _watchers = new(StringComparer.Ordinal); // pairId -> local watcher
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);            // pairId -> remote poll
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);                    // pairIds mid-sync
    private bool _disposed;

    /// <summary>Raised (off the UI thread) when an automatic full sync for a pair begins.</summary>
    public event Action<string>? SyncStarted;

    /// <summary>Raised (off the UI thread) when an automatic full sync for a pair finishes.</summary>
    public event Action<FullSyncEventArgs>? SyncCompleted;

    /// <summary>True if this pair is currently being auto-synced.</summary>
    public bool IsAutoSyncing(string pairId)
    {
        lock (_watchers)
        {
            return _watchers.ContainsKey(pairId);
        }
    }

    /// <summary>
    /// Starts automatic two-way sync for a full-sync pair: a debounced watcher on the local folder plus a
    /// periodic Drive poll, both running <see cref="SyncEngine.PlanAsync"/>+<see cref="SyncEngine.ApplyAsync"/>.
    /// Idempotent per pair.
    /// </summary>
    public void StartAutoSync(string accountId, SyncPair pair)
    {
        lock (_watchers)
        {
            if (_disposed || _watchers.ContainsKey(pair.Id))
            {
                return;
            }

            _watchers[pair.Id] = new FolderWatcher(pair.LocalPath, ct => RunAsync(accountId, pair, ct));
        }

        lock (_timers)
        {
            if (_disposed || _timers.ContainsKey(pair.Id))
            {
                return;
            }

            _timers[pair.Id] = new Timer(_ => _ = RunAsync(accountId, pair, CancellationToken.None), null, PollStartDelay, PollInterval);
        }
    }

    /// <summary>Stops automatic sync (local watcher + remote poll) for a pair.</summary>
    public void StopAutoSync(string pairId)
    {
        FolderWatcher? watcher;
        lock (_watchers)
        {
            _watchers.Remove(pairId, out watcher);
        }

        watcher?.Dispose();

        Timer? timer;
        lock (_timers)
        {
            _timers.Remove(pairId, out timer);
        }

        timer?.Dispose();
    }

    // Runs one full sync for a pair, non-reentrant per pair (a watcher event fired while a sync is running —
    // including the local writes a download produces — is coalesced; it won't stack up). Best-effort:
    // errors are reported via the event, not thrown.
    private async Task RunAsync(string accountId, SyncPair pair, CancellationToken cancellationToken)
    {
        lock (_inFlight)
        {
            if (_disposed || !_inFlight.Add(pair.Id))
            {
                return;
            }
        }

        try
        {
            SyncStarted?.Invoke(pair.Id);

            var plan = await syncEngine.PlanAsync(accountId, pair, cancellationToken).ConfigureAwait(false);
            if (plan.Operations.Count == 0)
            {
                SyncCompleted?.Invoke(new FullSyncEventArgs(pair.Id, EmptyResult, 0, false, null));
                return;
            }

            var deletes = plan.Operations.Count(o => o.Kind is SyncOperationKind.DeleteLocal or SyncOperationKind.DeleteRemote);
            var totalKnown = Math.Max(plan.RemoteSnapshot.Entries.Count, plan.LocalSnapshot.Entries.Count);
            if (ExceedsAutoDeleteThreshold(deletes, totalKnown))
            {
                // Too many deletions (in absolute count or as a share of the known tree) to apply
                // unattended — hand it to the user's manual, confirmed sync.
                SyncCompleted?.Invoke(new FullSyncEventArgs(pair.Id, null, deletes, true, null));
                return;
            }

            var result = await syncEngine.ApplyAsync(accountId, plan, cancellationToken: cancellationToken).ConfigureAwait(false);
            SyncCompleted?.Invoke(new FullSyncEventArgs(pair.Id, result, 0, false, null));
        }
        catch (OperationCanceledException)
        {
            // Stopped mid-run — nothing to report.
        }
        catch (Exception ex)
        {
            SyncCompleted?.Invoke(new FullSyncEventArgs(pair.Id, null, 0, false, ex));
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.Remove(pair.Id);
            }
        }
    }

    /// <summary>
    /// Whether an auto-sync plan's deletions are too risky to apply unattended — either a flat count over
    /// <see cref="MaxAutoDeletes"/>, or (for trees with at least <see cref="MinTreeSizeForProportionGuard"/>
    /// known items) more than <see cref="ProportionAutoDeleteThreshold"/> of the known tree at once. Pure
    /// and public so it's directly testable (see <c>--deleteguardtest</c>) without a live Drive plan.
    /// </summary>
    public static bool ExceedsAutoDeleteThreshold(int deletes, int totalKnown)
        => deletes > MaxAutoDeletes
            || (totalKnown >= MinTreeSizeForProportionGuard && deletes > totalKnown * ProportionAutoDeleteThreshold);

    private static SyncResult EmptyResult => new() { Completed = 0, Skipped = 0, Failures = [] };

    public void Dispose()
    {
        List<FolderWatcher> watchers;
        lock (_watchers)
        {
            _disposed = true;
            watchers = [.. _watchers.Values];
            _watchers.Clear();
        }

        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        List<Timer> timers;
        lock (_timers)
        {
            timers = [.. _timers.Values];
            _timers.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Dispose();
        }
    }
}
