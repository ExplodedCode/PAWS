namespace PAWS.Infrastructure.Storage;

/// <summary>Resolves the on-disk locations PAWS uses, rooted at <c>%LOCALAPPDATA%\PAWS</c>.</summary>
public sealed class PawsPaths
{
    public PawsPaths(string? rootOverride = null)
    {
        Root = rootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PAWS");
        SecretsDirectory = Path.Combine(Root, "secrets");
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\PAWS</c>. Deliberately the local (non-roaming) profile: DPAPI CurrentUser
    /// blobs can only be decrypted on the machine that wrote them, so roaming them would be useless.
    /// </summary>
    public string Root { get; }

    public string SecretsDirectory { get; }

    /// <summary>Non-secret per-pair sync state (last-known snapshot), e.g. <c>state\{pairId}.json</c>.</summary>
    public string StateDirectory => Path.Combine(Root, "state");

    /// <summary>Diagnostic logs (sync activity + failures), e.g. <c>logs\paws-20260701.log</c>.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Per-sync-pair last-known state file.</summary>
    public string StateFileFor(string pairId)
    {
        var safe = new string(pairId.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(safe))
        {
            throw new ArgumentException("Pair id must contain alphanumeric characters.", nameof(pairId));
        }

        return Path.Combine(StateDirectory, safe + ".json");
    }

    /// <summary>Per-pair set of populated (materialized) on-demand folders, e.g. <c>state\{pairId}.populated.json</c>.</summary>
    public string PopulatedFoldersFileFor(string pairId)
    {
        var safe = new string(pairId.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(safe))
        {
            throw new ArgumentException("Pair id must contain alphanumeric characters.", nameof(pairId));
        }

        return Path.Combine(StateDirectory, safe + ".populated.json");
    }

    /// <summary>Per-account encrypted secret blob, e.g. <c>secrets\{accountId}.bin</c>.</summary>
    public string SecretsFileFor(string accountId)
    {
        var safe = new string(accountId.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(safe))
        {
            throw new ArgumentException("Account id must contain alphanumeric characters.", nameof(accountId));
        }

        return Path.Combine(SecretsDirectory, safe + ".bin");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SecretsDirectory);
    }
}
