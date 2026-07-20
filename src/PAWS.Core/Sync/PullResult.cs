namespace PAWS.Core.Sync;

/// <summary>
/// Outcome of pulling remote changes down into an on-demand folder: how many placeholders were created
/// (new on Drive), updated (changed on Drive — reset to fresh placeholders), and deleted (removed on Drive).
/// </summary>
public sealed record PullResult(int Created, int Updated, int Deleted)
{
    public int Total => Created + Updated + Deleted;
}
