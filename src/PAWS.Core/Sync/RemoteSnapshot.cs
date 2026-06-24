namespace PAWS.Core.Sync;

/// <summary>
/// A point-in-time picture of a remote sync-root subtree: every file and folder under it, flattened and
/// keyed by relative path. Serializable so it can be persisted as the "last-known remote state" the
/// reconciler diffs the next snapshot (and the local tree) against. Does not include the root itself.
/// </summary>
public sealed record RemoteSnapshot
{
    /// <summary>The Drive path that was captured (the sync pair's RemotePath).</summary>
    public required string RootPath { get; init; }

    public required DateTimeOffset CapturedUtc { get; init; }

    /// <summary>All descendants of the root, sorted by <see cref="RemoteEntry.RelativePath"/> (ordinal).</summary>
    public required IReadOnlyList<RemoteEntry> Entries { get; init; }

    public int FileCount => Entries.Count(e => e.IsFile);

    public int FolderCount => Entries.Count(e => e.IsFolder);

    public long TotalFileBytes => Entries.Where(e => e.IsFile).Sum(e => e.Size ?? 0);
}
