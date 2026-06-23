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

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string ProtonSecretsFile => Path.Combine(SecretsDirectory, "proton.bin");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SecretsDirectory);
    }
}
