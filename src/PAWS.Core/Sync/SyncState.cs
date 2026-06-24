namespace PAWS.Core.Sync;

/// <summary>
/// What a single path looked like the last time it was successfully synced — the third input to the
/// reconciler (alongside the current remote and local snapshots). Comparing the live snapshots to this
/// is what lets the engine tell "created on one side" from "deleted on the other".
/// </summary>
public sealed record SyncStateEntry
{
    public required string RelativePath { get; init; }

    public required bool IsFolder { get; init; }

    /// <summary>Remote node UID at last sync.</summary>
    public string? RemoteUid { get; init; }

    /// <summary>Remote active-revision UID at last sync — a change here means the remote file changed.</summary>
    public string? RemoteRevisionUid { get; init; }

    /// <summary>Local file size at last sync.</summary>
    public long? Size { get; init; }

    /// <summary>Local last-write time at last sync — a change in size/time means the local file changed.</summary>
    public DateTimeOffset? LocalModifiedUtc { get; init; }
}

/// <summary>
/// The persisted "last-known" state for one sync pair: the agreed-upon picture after the previous sync.
/// Serializable (JSON) for storage alongside settings; empty on first sync.
/// </summary>
public sealed record SyncState
{
    public required string PairId { get; init; }

    public DateTimeOffset? LastSyncUtc { get; init; }

    public required IReadOnlyList<SyncStateEntry> Entries { get; init; }

    public static SyncState Empty(string pairId) => new() { PairId = pairId, Entries = [] };
}
