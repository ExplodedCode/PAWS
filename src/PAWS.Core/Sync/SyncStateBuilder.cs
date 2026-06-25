namespace PAWS.Core.Sync;

/// <summary>
/// Builds the new last-known <see cref="SyncState"/> from the post-sync remote and local snapshots.
/// Records only paths present (and of the same kind) on both sides — i.e. the agreed, in-sync set.
/// Paths that remained one-sided (a skipped conflict, or a failed operation) are intentionally omitted
/// so the next reconcile reconsiders them.
/// </summary>
public static class SyncStateBuilder
{
    public static SyncState Build(string pairId, RemoteSnapshot remote, LocalSnapshot local)
    {
        var localByPath = local.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

        var entries = new List<SyncStateEntry>();
        foreach (var r in remote.Entries)
        {
            if (!localByPath.TryGetValue(r.RelativePath, out var l) || r.IsFolder != l.IsFolder)
            {
                continue;
            }

            entries.Add(new SyncStateEntry
            {
                RelativePath = r.RelativePath,
                IsFolder = r.IsFolder,
                RemoteUid = r.Uid,
                RemoteRevisionUid = r.RevisionUid,
                Size = l.Size,
                LocalModifiedUtc = l.ModifiedUtc,
            });
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        return new SyncState
        {
            PairId = pairId,
            LastSyncUtc = DateTimeOffset.UtcNow,
            Entries = entries,
        };
    }
}
