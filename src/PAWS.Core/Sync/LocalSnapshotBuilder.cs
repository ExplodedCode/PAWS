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
    /// </summary>
    public LocalSnapshot? Capture(string localRoot, CancellationToken cancellationToken = default)
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
                // Skip reparse points (symlinks/junctions) to avoid cycles.
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
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

                if (info is DirectoryInfo subFolder)
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
