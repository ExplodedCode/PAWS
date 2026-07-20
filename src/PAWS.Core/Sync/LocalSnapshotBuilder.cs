namespace PAWS.Core.Sync;

/// <summary>
/// Walks a local folder subtree into a flat, sorted <see cref="LocalSnapshot"/>. Synchronous (local
/// file I/O); call from a background thread when invoked from the UI. Paths are normalized to
/// forward slashes so they pair directly with <see cref="RemoteEntry"/> relative paths.
/// </summary>
public sealed class LocalSnapshotBuilder
{
    /// <summary>
    /// Captures the subtree rooted at <paramref name="localRoot"/>. Returns null if the directory does
    /// not exist. Skips reparse points (symlinks/junctions) to avoid cycles and surprises.
    /// <para>When <paramref name="populatedFolders"/> is non-null (on-demand, lazy population), descent is
    /// limited to folders in that set (relative, '/'-separated, "" = root): a sub-folder placeholder is
    /// still recorded, but its children are NOT enumerated unless it's populated. This keeps the walk from
    /// (a) triggering on-demand population of un-browsed folders and (b) reporting their un-materialized
    /// contents as local deletions. Null = walk everything (full-sync mirror).</para>
    /// </summary>
    public LocalSnapshot? Capture(string localRoot, IReadOnlySet<string>? populatedFolders = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(localRoot))
        {
            return null;
        }

        var root = new DirectoryInfo(localRoot);
        var entries = new List<LocalEntry>();
        var folders = new Queue<DirectoryInfo>();
        folders.Enqueue(root);

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Dequeue();

            foreach (var info in folder.EnumerateFileSystemInfos())
            {
                // Skip symlinks/junctions (cycle risk) — but NOT Cloud Filter placeholders, which are
                // also reparse points yet are real files we must sync. Symlinks report a LinkTarget;
                // cloud placeholders do not.
                if (info.LinkTarget is not null)
                {
                    continue;
                }

                var isFolder = info is DirectoryInfo;
                var relativePath = Path.GetRelativePath(localRoot, info.FullName).Replace('\\', '/');

                entries.Add(new LocalEntry
                {
                    RelativePath = relativePath,
                    Name = info.Name,
                    IsFolder = isFolder,
                    Size = info is FileInfo file ? file.Length : null,
                    ModifiedUtc = isFolder ? null : new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                });

                // Descend only into populated folders (when scoping is on) — see the summary. An
                // un-populated sub-folder is recorded above but not walked.
                if (info is DirectoryInfo subFolder && (populatedFolders is null || populatedFolders.Contains(relativePath)))
                {
                    folders.Enqueue(subFolder);
                }
            }
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        return new LocalSnapshot
        {
            RootPath = localRoot,
            CapturedUtc = DateTimeOffset.UtcNow,
            Entries = entries,
        };
    }
}
