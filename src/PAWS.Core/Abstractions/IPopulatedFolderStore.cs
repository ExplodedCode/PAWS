namespace PAWS.Core.Abstractions;

/// <summary>
/// Persists, per sync pair, the set of on-demand folders that have been POPULATED (materialized as
/// placeholders locally). Relative paths, '/'-separated, with the empty string meaning the sync root.
/// Push and pull consult this so they can tell an un-browsed folder (whose contents were never
/// materialized) from a genuinely emptied/deleted one — the guard that stops lazy population from being
/// mistaken for local deletions and trashing remote content.
/// </summary>
public interface IPopulatedFolderStore
{
    /// <summary>The populated folder set for a pair (empty if none recorded yet).</summary>
    ISet<string> Load(string pairId);

    void Save(string pairId, IReadOnlySet<string> folders);

    void Clear(string pairId);
}
