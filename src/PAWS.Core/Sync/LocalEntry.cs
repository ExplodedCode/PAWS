namespace PAWS.Core.Sync;

/// <summary>
/// One node in a <see cref="LocalSnapshot"/>: a file or folder under the local sync root, keyed by its
/// <see cref="RelativePath"/> (forward-slash separated, relative to the root). The reconciler pairs
/// local and remote entries by this path.
/// </summary>
public sealed record LocalEntry
{
    /// <summary>Path relative to the local root, '/'-separated, no leading slash (e.g. <c>Docs/report.pdf</c>).</summary>
    public required string RelativePath { get; init; }

    public required string Name { get; init; }

    public required bool IsFolder { get; init; }

    public bool IsFile => !IsFolder;

    /// <summary>File size in bytes (files only).</summary>
    public long? Size { get; init; }

    /// <summary>Last-write time in UTC (files only).</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }
}
