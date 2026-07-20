using PAWS.Core.Sync;

namespace PAWS.Core.Abstractions;

/// <summary>
/// Persists each sync pair's last-known <see cref="SyncState"/> (non-secret metadata). Keyed by pair id.
/// </summary>
public interface ISyncStateStore
{
    /// <summary>Loads the saved state for a pair, or null if it has never synced.</summary>
    SyncState? Load(string pairId);

    void Save(SyncState state);

    void Clear(string pairId);
}
