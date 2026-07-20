using System.Text.Json;
using System.Text.Json.Serialization;
using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;

namespace PAWS.Infrastructure.Storage;

/// <summary><see cref="ISettingsStore"/> that persists <see cref="PawsSettings"/> as indented JSON.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly PawsPaths _paths;

    public JsonSettingsStore(PawsPaths paths) => _paths = paths;

    public PawsSettings Load()
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new PawsSettings();
        }

        var json = File.ReadAllText(_paths.SettingsFile);
        return JsonSerializer.Deserialize<PawsSettings>(json, JsonOptions) ?? new PawsSettings();
    }

    public void Save(PawsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _paths.EnsureCreated();

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tmp = _paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _paths.SettingsFile, overwrite: true);
    }
}
