namespace PAWS.Core.Sync;

/// <summary>How the user chose to settle one conflicted path (see <see cref="SyncOperationKind.Conflict"/>).</summary>
public enum ConflictResolution
{
    /// <summary>
    /// This PC wins: upload the local file over the remote one — or, when the conflict is "deleted
    /// locally but remote changed", keep it deleted by removing the remote copy too (trash).
    /// </summary>
    KeepLocal,

    /// <summary>
    /// Proton Drive wins: bring the remote version down over the local one — or, when the conflict is
    /// "deleted remotely but local changed", accept the deletion by removing the local copy.
    /// </summary>
    KeepRemote,

    /// <summary>
    /// Keep both versions: the local file is renamed to a "(conflict copy …)" sibling (which uploads as
    /// its own file on the next sync) and the remote version takes the original name.
    /// </summary>
    KeepBoth,
}

/// <summary>
/// The concrete steps a chosen <see cref="ConflictResolution"/> turns into:
/// optionally rename the local file to a conflict-copy sibling first, then run the listed plain
/// (non-conflict) operations. Produced by <see cref="SyncExecutor.PlanResolution"/>.
/// </summary>
public sealed record ConflictPlan(bool RenameLocalToConflictCopy, IReadOnlyList<SyncOperation> Operations);

/// <summary>Outcome of resolving conflicts on an on-demand pair.</summary>
public sealed record ConflictResolveResult(int Resolved, IReadOnlyList<string> Errors);
