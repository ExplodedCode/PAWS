using PAWS.Core.Drive;

namespace PAWS.Core.Sync;

/// <summary>
/// Walks a remote sync-root subtree via <see cref="IProtonDriveClient"/> and produces a flat, sorted
/// <see cref="RemoteSnapshot"/>. Breadth-first so large trees stream a folder at a time; the result is
/// sorted by relative path for deterministic, diff-friendly snapshots.
/// </summary>
public sealed class RemoteSnapshotBuilder(IProtonDriveClient client)
{
    /// <summary>
    /// Captures the subtree rooted at <paramref name="remotePath"/>. Returns null if the path does not
    /// resolve to a folder. Reports each entry to <paramref name="onEntry"/> as it is discovered (e.g.
    /// for progress), if provided.
    /// </summary>
    public async Task<RemoteSnapshot?> CaptureAsync(
        string remotePath,
        Action<RemoteEntry>? onEntry = null,
        CancellationToken cancellationToken = default)
    {
        var root = await client.ResolvePathAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (root is null || !root.IsFolder)
        {
            return null;
        }

        var entries = new List<RemoteEntry>();
        var folders = new Queue<(RemoteNode Node, string RelativeBase)>();
        folders.Enqueue((root, string.Empty));

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, relativeBase) = folders.Dequeue();

            await foreach (var child in client.ListChildrenAsync(folder, cancellationToken).ConfigureAwait(false))
            {
                var relativePath = relativeBase.Length == 0 ? child.Name : $"{relativeBase}/{child.Name}";

                var entry = new RemoteEntry
                {
                    RelativePath = relativePath,
                    Name = child.Name,
                    IsFolder = child.IsFolder,
                    Size = child.Size,
                    ModifiedUtc = child.ModifiedUtc,
                    Uid = child.Uid,
                    ParentUid = child.ParentUid,
                    RevisionUid = child.RevisionUid,
                };

                entries.Add(entry);
                onEntry?.Invoke(entry);

                if (child.IsFolder)
                {
                    folders.Enqueue((child, relativePath));
                }
            }
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        return new RemoteSnapshot
        {
            RootPath = remotePath,
            CapturedUtc = DateTimeOffset.UtcNow,
            Entries = entries,
        };
    }
}
