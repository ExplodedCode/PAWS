namespace PAWS.Core.Sync;

/// <summary>
/// One node in a <see cref="RemoteSnapshot"/>: a file or folder somewhere under the sync root, keyed by
/// its <see cref="RelativePath"/> (forward-slash separated, relative to the root — the reconciler pairs
/// remote and local entries by this path). Carries the SDK identity strings so the sync engine can act
/// on the node without re-resolving it.
/// </summary>
public sealed record RemoteEntry
{
    /// <summary>Path relative to the sync root, '/'-separated, no leading slash (e.g. <c>Docs/report.pdf</c>).</summary>
    public required string RelativePath { get; init; }

    /// <summary>The final path segment (decrypted name).</summary>
    public required string Name { get; init; }

    public required bool IsFolder { get; init; }

    public bool IsFile => !IsFolder;

    /// <summary>Active-revision size in bytes, when known (files only).</summary>
    public long? Size { get; init; }

    /// <summary>Active-revision last-modification time, when known (files only).</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>Node composite UID (<c>volumeId~linkId</c>).</summary>
    public required string Uid { get; init; }

    /// <summary>Parent folder's node UID.</summary>
    public string? ParentUid { get; init; }

    /// <summary>Active-revision composite UID — needed to download content (files only).</summary>
    public string? RevisionUid { get; init; }
}
