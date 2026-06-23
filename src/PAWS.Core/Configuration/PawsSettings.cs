namespace PAWS.Core.Configuration;

/// <summary>
/// Non-secret PAWS configuration, persisted as <c>settings.json</c>.
/// Anything sensitive (passwords, session tokens) lives in <see cref="PAWS.Core.Abstractions.ISecretStore"/> instead.
/// </summary>
public sealed class PawsSettings
{
    /// <summary>Schema version, for future migrations.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The Proton account this machine is linked to (identification/display only).</summary>
    public string? AccountEmail { get; set; }

    /// <summary>Proton Drive API base URL. Default taken from the Proton SDK (<c>ProtonApiDefaults.BaseUrl</c>).</summary>
    public string ApiBaseUrl { get; set; } = "https://drive-api.proton.me/";

    /// <summary>Configured folder mappings.</summary>
    public List<SyncPair> SyncPairs { get; set; } = new();

    /// <summary>True once initial setup and a successful authentication have completed.</summary>
    public bool SetupCompleted { get; set; }
}
