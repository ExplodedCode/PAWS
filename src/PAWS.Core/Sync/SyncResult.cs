namespace PAWS.Core.Sync;

/// <summary>Progress of a sync run, reported before each operation is applied.</summary>
public sealed record SyncProgress(int Completed, int Total, SyncOperation Current);

/// <summary>An operation that failed during execution, with the reason.</summary>
public sealed record SyncFailure(SyncOperation Operation, string Error);

/// <summary>Outcome of applying a sync plan.</summary>
public sealed record SyncResult
{
    /// <summary>Operations applied successfully.</summary>
    public required int Completed { get; init; }

    /// <summary>Operations deliberately not applied (conflicts).</summary>
    public required int Skipped { get; init; }

    public required IReadOnlyList<SyncFailure> Failures { get; init; }

    public int Total => Completed + Skipped + Failures.Count;

    public bool AllSucceeded => Failures.Count == 0;
}
