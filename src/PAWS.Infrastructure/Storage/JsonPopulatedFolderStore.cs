using System.Text.Json;
using PAWS.Core.Abstractions;

namespace PAWS.Infrastructure.Storage;

/// <summary>
/// Stores each pair's populated on-demand folder set as plain JSON under <c>%LOCALAPPDATA%\PAWS\state</c>
/// (e.g. <c>{pairId}.populated.json</c>). Non-secret. Corrupt/unreadable = "nothing populated" (safe: the
/// worst case is push/pull treats folders as un-browsed and simply skips them until re-browsed).
/// </summary>
public sealed class JsonPopulatedFolderStore(PawsPaths paths) : IPopulatedFolderStore
{
    private readonly object _gate = new();

    public ISet<string> Load(string pairId)
    {
        var file = paths.PopulatedFoldersFileFor(pairId);
        lock (_gate)
        {
            if (!File.Exists(file))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            try
            {
                var folders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [];
                return new HashSet<string>(folders, StringComparer.Ordinal);
            }
            catch
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }
    }

    public void Save(string pairId, IReadOnlySet<string> folders)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(paths.StateDirectory);
            var ordered = folders.OrderBy(f => f, StringComparer.Ordinal).ToList();
            File.WriteAllText(paths.PopulatedFoldersFileFor(pairId), JsonSerializer.Serialize(ordered));
        }
    }

    public void Clear(string pairId)
    {
        lock (_gate)
        {
            var file = paths.PopulatedFoldersFileFor(pairId);
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }
}
