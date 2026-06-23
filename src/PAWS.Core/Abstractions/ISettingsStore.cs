using PAWS.Core.Configuration;

namespace PAWS.Core.Abstractions;

/// <summary>Persists non-secret <see cref="PawsSettings"/> (e.g. as settings.json).</summary>
public interface ISettingsStore
{
    PawsSettings Load();

    void Save(PawsSettings settings);
}
