namespace PAWS.Core.Sync;

/// <summary>The kind of action the reconciler decided a path needs to become consistent.</summary>
public enum SyncOperationKind
{
    /// <summary>Remote file is new or newer — copy it down to local.</summary>
    DownloadFile,

    /// <summary>Local file is new or newer — upload it to remote.</summary>
    UploadFile,

    /// <summary>Folder exists remotely but not locally — create it locally.</summary>
    CreateLocalFolder,

    /// <summary>Folder exists locally but not remotely — create it on remote.</summary>
    CreateRemoteFolder,

    /// <summary>Path was deleted locally (and remote is unchanged) — remove it from remote (trash).</summary>
    DeleteRemote,

    /// <summary>Path was deleted remotely (and local is unchanged) — remove it locally.</summary>
    DeleteLocal,

    /// <summary>Both sides changed (or independently created different content) — needs resolution.</summary>
    Conflict,
}

/// <summary>
/// One unit of work the reconciler emitted for a path. Pure data: the executor reads <see cref="Remote"/>
/// /<see cref="Local"/> to actually move bytes. <see cref="Reason"/> is a human-readable explanation for
/// previews and logs.
/// </summary>
public sealed record SyncOperation
{
    public required SyncOperationKind Kind { get; init; }

    public required string RelativePath { get; init; }

    public required bool IsFolder { get; init; }

    public string? Reason { get; init; }

    /// <summary>The remote entry involved, when the operation reads from / acts on remote.</summary>
    public RemoteEntry? Remote { get; init; }

    /// <summary>The local entry involved, when the operation reads from / acts on local.</summary>
    public LocalEntry? Local { get; init; }
}
