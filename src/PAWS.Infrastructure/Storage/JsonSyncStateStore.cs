using System.Text.Json;
using PAWS.Core.Abstractions;
using PAWS.Core.Sync;

namespace PAWS.Infrastructure.Storage;

/// <summary>
/// Stores each pair's last-known <see cref="SyncState"/> as plain JSON under <c>%LOCALAPPDATA%\PAWS\state</c>.
/// Non-secret (no credentials), so unlike the secret store it is not DPAPI-encrypted. Corrupt or
/// unreadable state is treated as "no state" — the reconciler simply recomputes from scratch.
/// </summary>
public sealed class JsonSyncStateStore(PawsPaths paths) : ISyncStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public SyncState? Load(string pairId)
    {
        var file = paths.StateFileFor(pairId);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(file), Options);
        }
        catch
        {
            return null;
        }
    }

    public void Save(SyncState state)
    {
        Directory.CreateDirectory(paths.StateDirectory);
        File.WriteAllText(paths.StateFileFor(state.PairId), JsonSerializer.Serialize(state, Options));
    }

    public void Clear(string pairId)
    {
        var file = paths.StateFileFor(pairId);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}
