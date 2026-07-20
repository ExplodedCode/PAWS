using PAWS.Core.Configuration;
using PAWS.Core.Drive;

namespace PAWS.Core.Sync;

/// <summary>
/// A computed-but-not-yet-applied sync plan for one pair: the captured snapshots, the resolved remote
/// root node, and the operations the reconciler produced. Carries everything the executor needs, so the
/// UI can show the plan and apply exactly what was shown after the user confirms.
/// </summary>
public sealed record SyncPlan(
    SyncPair Pair,
    RemoteSnapshot RemoteSnapshot,
    LocalSnapshot LocalSnapshot,
    RemoteNode RemoteRoot,
    IReadOnlyList<SyncOperation> Operations);
