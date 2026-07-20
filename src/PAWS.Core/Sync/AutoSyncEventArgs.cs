namespace PAWS.Core.Sync;

/// <summary>
/// Reported by <see cref="CloudSyncService"/> when an automatic (watcher-triggered) push for a pair
/// finishes. Either <see cref="Result"/> (the push outcome) or <see cref="Error"/> is set, never both.
/// </summary>
public sealed record AutoSyncEventArgs(string PairId, SyncResult? Result, Exception? Error)
{
    public bool Succeeded => Error is null;
}
