namespace PAWS.Core.Configuration;

/// <summary>
/// A configured Proton account connection. Keyed by <see cref="Id"/> (NOT email), so the same
/// Proton account can be added more than once if the user wants separate entries. Each account
/// owns its own credentials (in the secret store, keyed by <see cref="Id"/>) and its own set of
/// folder mappings — supporting both "one account, many folders" and "many accounts at once".
/// </summary>
public sealed class ProtonAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Proton login email. Not unique across accounts.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Optional friendly label to disambiguate entries (e.g. "Work", "Personal").</summary>
    public string? DisplayName { get; set; }

    /// <summary>Folder mappings belonging to this account.</summary>
    public List<SyncPair> SyncPairs { get; set; } = new();

    /// <summary>Display string: the label if set, otherwise the email.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Email : $"{DisplayName} ({Email})";
}
