namespace PAWS.Core.Configuration;

/// <summary>
/// Non-secret PAWS configuration, persisted as <c>settings.json</c>. Holds one or more
/// <see cref="ProtonAccount"/>s, each with its own folder mappings. Anything sensitive
/// (passwords, session tokens) lives in <see cref="PAWS.Core.Abstractions.ISecretStore"/>,
/// keyed per account.
/// </summary>
public sealed class PawsSettings
{
    /// <summary>Schema version, for future migrations. v2 introduced multi-account.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Proton Drive API base URL. Default taken from the Proton SDK (<c>ProtonApiDefaults.BaseUrl</c>).</summary>
    public string ApiBaseUrl { get; set; } = "https://drive-api.proton.me/";

    /// <summary>All configured accounts. Multiple are supported, including duplicate emails.</summary>
    public List<ProtonAccount> Accounts { get; set; } = new();

    /// <summary>True once at least one account has been added.</summary>
    public bool HasAnyAccount => Accounts.Count > 0;
}
