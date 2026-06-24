namespace PAWS.Core.Drive;

/// <summary>
/// A file or folder in Proton Drive, as the rest of PAWS sees it. Deliberately BCL-only: identity is
/// carried as the SDK's opaque composite-UID strings (<see cref="Uid"/> = <c>volumeId~linkId</c>,
/// <see cref="RevisionUid"/> = <c>volumeId~linkId~revisionId</c>), which the adapter parses back into
/// SDK types. No SDK type leaks into Core, so the reconciler and Cloud Filter engine stay testable.
/// </summary>
public sealed record RemoteNode
{
    /// <summary>The node's composite UID (<c>volumeId~linkId</c>); stable identifier for all operations.</summary>
    public required string Uid { get; init; }

    /// <summary>The containing folder's <see cref="Uid"/>, or null for the "My files" root.</summary>
    public string? ParentUid { get; init; }

    /// <summary>Decrypted name (single path segment, not a full path).</summary>
    public required string Name { get; init; }

    public required bool IsFolder { get; init; }

    public bool IsFile => !IsFolder;

    /// <summary>Size of the active revision in bytes, when known (files only).</summary>
    public long? Size { get; init; }

    /// <summary>Last-modification time claimed by the active revision, when known (files only).</summary>
    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>
    /// The active revision's composite UID (files only); needed to download content or upload a new
    /// revision. Null for folders.
    /// </summary>
    public string? RevisionUid { get; init; }
}
