namespace PAWS.Core.Sync;

/// <summary>
/// Reported by <see cref="CloudSyncService"/> when a periodic auto-pull for a pair finishes. Either
/// <see cref="Result"/> (the pull outcome) or <see cref="Error"/> is set, never both.
/// </summary>
public sealed record AutoPullEventArgs(string PairId, PullResult? Result, Exception? Error)
{
    public bool Succeeded => Error is null;
}
