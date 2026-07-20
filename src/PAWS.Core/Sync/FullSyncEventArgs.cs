namespace PAWS.Core.Sync;

/// <summary>
/// Reported by <see cref="FullSyncService"/> when an automatic full (two-way) sync for a pair finishes.
/// Exactly one situation applies:
/// <list type="bullet">
/// <item><see cref="Result"/> set, <see cref="NeedsReview"/> false — the plan was applied (may be 0 ops).</item>
/// <item><see cref="NeedsReview"/> true — the plan wanted to delete <see cref="PendingDeletes"/> items,
/// over the safety limit, so it was NOT applied; the user should review via manual "Sync now".</item>
/// <item><see cref="Error"/> set — the sync failed.</item>
/// </list>
/// </summary>
public sealed record FullSyncEventArgs(string PairId, SyncResult? Result, int PendingDeletes, bool NeedsReview, Exception? Error)
{
    public bool Succeeded => Error is null && !NeedsReview;
}
