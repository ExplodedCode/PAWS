namespace PAWS.Core.Sync;

/// <summary>
/// A point-in-time picture of the local sync-root subtree, flattened and keyed by relative path —
/// the local counterpart of <see cref="RemoteSnapshot"/> that the reconciler diffs against it.
/// Does not include the root folder itself.
/// </summary>
public sealed record LocalSnapshot
{
    public required string RootPath { get; init; }

    public required DateTimeOffset CapturedUtc { get; init; }

    /// <summary>All descendants of the root, sorted by <see cref="LocalEntry.RelativePath"/> (ordinal).</summary>
    public required IReadOnlyList<LocalEntry> Entries { get; init; }

    public int FileCount => Entries.Count(e => e.IsFile);

    public int FolderCount => Entries.Count(e => e.IsFolder);

    public long TotalFileBytes => Entries.Where(e => e.IsFile).Sum(e => e.Size ?? 0);
}
