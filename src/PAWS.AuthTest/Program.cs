using System.Diagnostics;
using System.Text;
using PAWS.CloudFilter;
using PAWS.Core.Configuration;
using PAWS.Core.Drive;
using PAWS.Core.Sync;
using PAWS.Infrastructure.Proton;
using PAWS.Infrastructure.Storage;
using PAWS.Proton;
using PAWS.Proton.Drive;

Console.OutputEncoding = Encoding.UTF8;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "drive";
var doWrite = args.Contains("--write", StringComparer.OrdinalIgnoreCase);

// First non-flag argument after the mode (e.g. a remote path for --snapshot); defaults to the root.
var pathArg = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "/";

// Optional explicit local + remote for `--sync <local> <remote>` (safe testing of an arbitrary pair).
var localArg = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
var remoteArg = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;

return mode switch
{
    "--cryptocheck" or "cryptocheck" => CryptoCheck(),
    "--snapshot" or "snapshot" => await SnapshotAsync(pathArg),
    "--trash" or "trash" => await TrashPathAsync(pathArg),
    "--plan" or "plan" => await PlanAsync(),
    "--plantest" or "plantest" => ReconcileSelfTest(),
    "--sync" or "sync" => await SyncAsync(localArg, remoteArg),
    "--placeholders" or "placeholders" => await PlaceholdersAsync(localArg, remoteArg),
    "--hydrate" or "hydrate" => await HydrateAsync(localArg, remoteArg),
    "--streamtest" or "streamtest" => await StreamTestAsync(localArg, remoteArg),
    "--uploadtest" or "uploadtest" => await UploadTestAsync(localArg, remoteArg),
    "--drafttest" or "drafttest" => await DraftTestAsync(localArg),
    "--pushtest" or "pushtest" => await PushTestAsync(localArg, remoteArg),
    "--watchtest" or "watchtest" => await WatchTestAsync(localArg, remoteArg),
    "--pulltest" or "pulltest" => await PullTestAsync(localArg, remoteArg),
    "--fullautotest" or "fullautotest" => await FullAutoTestAsync(localArg, remoteArg),
    "--deltest" or "deltest" => await DeleteConsistencyTestAsync(localArg, remoteArg),
    "--freshpoll" or "freshpoll" => await FreshClientPollTestAsync(pathArg),
    "--unregister" or "unregister" => Unregister(localArg),
    _ => await DriveAsync(doWrite),
};

// Phase 3b: full on-demand round-trip — register + placeholders + connect the provider, then READ a
// placeholder (which triggers hydration: download from Drive and feed it back), and verify the content.
static async Task<int> HydrateAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --hydrate <localFolder> <remotePath>");
        return 1;
    }

    Console.WriteLine($"PAWS - hydration test\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var engine = new CloudFilterPlaceholderEngine();
    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    var snapshot = await new RemoteSnapshotBuilder(drive).CaptureAsync(remoteOverride).ConfigureAwait(false);
    if (snapshot is null)
    {
        Console.WriteLine($"  x Remote path is not a folder: {remoteOverride}");
        return 1;
    }

    var firstFile = snapshot.Entries.FirstOrDefault(e => e.IsFile && e.RevisionUid is not null);
    if (firstFile is null)
    {
        Console.WriteLine("  x No file in the remote folder to hydrate. Put a file in it and retry.");
        return 1;
    }

    engine.RegisterSyncRoot(new SyncRootInfo(localOverride, "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64", "PAWS", "1.0.0.0"));

    // Eagerly create placeholders so the root shows content (the sync root doesn't fire
    // FETCH_PLACEHOLDERS for its own children); the connected provider still answers FETCH_PLACEHOLDERS
    // (for safety / sub-population) and FETCH_DATA (download on open).
    var created = engine.CreatePlaceholders(localOverride, snapshot);
    Console.WriteLine($"Placeholders: created {created.Created}, errors {created.Errors.Count}");

    using var connection = engine.Connect(localOverride, snapshot, async (identity, output, ct) =>
    {
        await drive.DownloadAsync(NodeFromIdentity(identity), output, cancellationToken: ct).ConfigureAwait(false);
    });
    Console.WriteLine("Provider connected.\n");

    // Enumerate the tree — this triggers FETCH_PLACEHOLDERS (the part that hung Explorer before).
    Console.WriteLine("Enumerating folder (FETCH_PLACEHOLDERS)…");
    var entries = Directory.EnumerateFileSystemEntries(localOverride, "*", SearchOption.AllDirectories).Order().ToList();
    foreach (var entry in entries)
    {
        var isDir = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
        Console.WriteLine($"   {(isDir ? "DIR " : "file")} {Path.GetRelativePath(localOverride, entry)}");
    }

    Console.WriteLine($"\n  + Enumerated {entries.Count} entr(ies) without hanging.\n");

    var localFile = entries.FirstOrDefault(e => (File.GetAttributes(e) & FileAttributes.Directory) == 0);
    if (localFile is not null)
    {
        Console.WriteLine($"Reading \"{Path.GetRelativePath(localOverride, localFile)}\" — this triggers a download (FETCH_DATA)…");
        try
        {
            var bytes = await File.ReadAllBytesAsync(localFile).ConfigureAwait(false);
            var preview = Encoding.UTF8.GetString(bytes, 0, Math.Min(80, bytes.Length)).Replace('\n', ' ');
            Console.WriteLine($"  + Hydrated {bytes.Length} B.  preview: {preview}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  x Read failed: {ex.Message}");
        }
    }

    connection.Dispose();
    engine.UnregisterSyncRoot(localOverride);
    Console.WriteLine("\nDisconnected + unregistered.");
    return 0;
}

// Streaming-hydration correctness: upload a multi-MB random file (size deliberately NOT a multiple of the
// 1 MiB transfer chunk), hydrate it through the streaming provider, and verify every byte round-trips
// (SHA-256). Exercises the multi-chunk / running-offset path that a tiny file can't. `--streamtest <local> <remote>`.
static async Task<int> StreamTestAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --streamtest <localFolder> <remotePath>");
        return 1;
    }

    Console.WriteLine($"PAWS - streaming hydration test\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var engine = new CloudFilterPlaceholderEngine();
    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    // ~5 MB, offset by a non-round amount so chunk boundaries don't line up on 1 MiB.
    var payload = new byte[(5 * 1024 * 1024) + 12345];
    Random.Shared.NextBytes(payload);
    var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
    var newName = $"streamtest-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bin";

    var folder = await drive.ResolvePathAsync(remoteOverride).ConfigureAwait(false);
    if (folder is null)
    {
        Console.WriteLine($"  x Remote folder not found: {remoteOverride}");
        return 1;
    }

    using (var content = new MemoryStream(payload))
    {
        await drive.UploadAsync(folder, newName, content).ConfigureAwait(false);
    }

    Console.WriteLine($"  uploaded {newName} ({payload.Length:N0} B), sha256 {expected[..16]}…");

    var snapshot = await new RemoteSnapshotBuilder(drive).CaptureAsync(remoteOverride).ConfigureAwait(false);
    if (snapshot is null || snapshot.Entries.All(e => e.Name != newName))
    {
        Console.WriteLine("  x Uploaded file didn't appear in the snapshot.");
        return 1;
    }

    engine.RegisterSyncRoot(new SyncRootInfo(localOverride, "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64", "PAWS", "1.0.0.0"));
    engine.CreatePlaceholders(localOverride, snapshot);
    using var connection = engine.Connect(localOverride, snapshot, async (identity, output, ct) =>
    {
        await drive.DownloadAsync(NodeFromIdentity(identity), output, cancellationToken: ct).ConfigureAwait(false);
    });

    var localFile = Path.Combine(localOverride, newName);
    Console.WriteLine("  reading the placeholder (streaming hydration)…");
    var ok = false;
    try
    {
        var read = await File.ReadAllBytesAsync(localFile).ConfigureAwait(false);
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(read));
        ok = read.Length == payload.Length && string.Equals(actual, expected, StringComparison.Ordinal);
        Console.WriteLine($"  read {read.Length:N0} B, sha256 {actual[..16]}… → {(ok ? "MATCH" : "MISMATCH")}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Read failed: {ex.Message}");
    }

    connection.Dispose();
    engine.UnregisterSyncRoot(localOverride);

    await using (var verify = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        if (verify is not null)
        {
            var node = await verify.ResolvePathAsync($"{remoteOverride.TrimEnd('/')}/{newName}").ConfigureAwait(false);
            if (node is not null)
            {
                await verify.TrashAsync(node).ConfigureAwait(false);
                Console.WriteLine("  cleaned up the remote test file.");
            }
        }
    }

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// Reproduces the orphaned-draft collision: start an upload and cancel it mid-flight (leaves an incomplete
// DRAFT node holding the name, which listings hide), then re-upload the SAME name. With the
// overrideExistingDraftByOtherClient fix the retry should SUCCEED instead of throwing
// NodeWithSameNameExistsException. `--drafttest <remotePath>`.
static async Task<int> DraftTestAsync(string? remoteOverride)
{
    if (remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --drafttest <remotePath>");
        return 1;
    }

    Console.WriteLine($"PAWS - draft-collision test\n  remote : {remoteOverride}\n");

    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    var folder = await drive.ResolvePathAsync(remoteOverride).ConfigureAwait(false);
    if (folder is null)
    {
        Console.WriteLine($"  x Remote folder not found: {remoteOverride}");
        return 1;
    }

    var name = $"drafttest-{DateTime.UtcNow:HHmmss}.bin";
    var temp = Path.Combine(Path.GetTempPath(), name);
    var block = new byte[1024 * 1024];
    Random.Shared.NextBytes(block);
    await using (var fs = File.Create(temp))
    {
        for (var i = 0; i < 20; i++)
        {
            await fs.WriteAsync(block).ConfigureAwait(false);
        }
    }

    // Attempt 1: cancel mid-upload to leave a draft.
    using (var cts = new CancellationTokenSource())
    {
        var upload = Task.Run(async () =>
        {
            await using var content = File.OpenRead(temp);
            await drive.UploadAsync(folder, name, content, cancellationToken: cts.Token).ConfigureAwait(false);
        });

        await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
        cts.Cancel();
        try
        {
            await upload.ConfigureAwait(false);
            Console.WriteLine("  (attempt 1 finished before cancel took effect)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  interrupted attempt 1 ({ex.GetType().Name}) — an orphaned draft may now hold \"{name}\".");
        }
    }

    // Attempt 2: same name — should override the orphaned draft and succeed.
    var ok = false;
    try
    {
        await using var content = File.OpenRead(temp);
        await drive.UploadAsync(folder, name, content).ConfigureAwait(false);
        Console.WriteLine("  + Attempt 2 (same name) SUCCEEDED — draft override works.");
        ok = true;

        var node = await drive.ResolvePathAsync($"{remoteOverride.TrimEnd('/')}/{name}").ConfigureAwait(false);
        if (node is not null)
        {
            await drive.TrashAsync(node).ConfigureAwait(false);
            Console.WriteLine("  cleaned up.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Attempt 2 FAILED: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        try { File.Delete(temp); } catch { /* ignore */ }
    }

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// Repro tool for large-file upload failures: upload a temp file of a given size straight through the Drive
// client (no sync machinery) and print the FULL exception if it fails. `--uploadtest <remotePath> [sizeMB]`.
static async Task<int> UploadTestAsync(string? remoteOverride, string? sizeArg)
{
    if (remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --uploadtest <remotePath> [sizeMB]");
        return 1;
    }

    var sizeMb = int.TryParse(sizeArg, out var parsed) && parsed > 0 ? parsed : 50;
    Console.WriteLine($"PAWS - upload test\n  remote : {remoteOverride}\n  size   : {sizeMb} MB\n");

    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    var folder = await drive.ResolvePathAsync(remoteOverride).ConfigureAwait(false);
    if (folder is null)
    {
        Console.WriteLine($"  x Remote folder not found: {remoteOverride}");
        return 1;
    }

    var name = $"uploadtest-{sizeMb}MB-{DateTime.UtcNow:HHmmss}.bin";
    var temp = Path.Combine(Path.GetTempPath(), name);

    Console.WriteLine($"  writing {sizeMb} MB temp file…");
    var block = new byte[1024 * 1024];
    Random.Shared.NextBytes(block);
    await using (var fs = File.Create(temp))
    {
        for (var i = 0; i < sizeMb; i++)
        {
            await fs.WriteAsync(block).ConfigureAwait(false);
        }
    }

    var ok = false;
    try
    {
        var sw = Stopwatch.StartNew();
        await using (var content = File.OpenRead(temp))
        {
            await drive.UploadAsync(folder, name, content).ConfigureAwait(false);
        }

        sw.Stop();
        Console.WriteLine($"  + Upload OK in {sw.Elapsed.TotalSeconds:F1}s ({sizeMb / Math.Max(0.1, sw.Elapsed.TotalSeconds):F1} MB/s).");
        ok = true;

        var node = await drive.ResolvePathAsync($"{remoteOverride.TrimEnd('/')}/{name}").ConfigureAwait(false);
        if (node is not null)
        {
            await drive.TrashAsync(node).ConfigureAwait(false);
            Console.WriteLine("  cleaned up the remote test file.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x UPLOAD FAILED (full detail):\n{ex}");
    }
    finally
    {
        try { File.Delete(temp); } catch { /* ignore */ }
    }

    return ok ? 0 : 1;
}

// Rebuilds a minimal RemoteNode from a placeholder's stored identity (a revision uid "vol~link~rev").
static RemoteNode NodeFromIdentity(string identity)
{
    var lastTilde = identity.LastIndexOf('~');
    var nodeUid = lastTilde > 0 ? identity[..lastTilde] : identity;
    return new RemoteNode { Uid = nodeUid, RevisionUid = identity, Name = string.Empty, IsFolder = false };
}

// Phase 3c: two-way on-demand. Enable on-demand on a folder, add a new local file, push changes up,
// and verify it reached Drive. `--pushtest <localFolder> <remotePath>`.
static async Task<int> PushTestAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --pushtest <localFolder> <remotePath>");
        return 1;
    }

    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured.");
        return 1;
    }

    Console.WriteLine($"PAWS - on-demand push test\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var engine = new CloudFilterPlaceholderEngine();
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new SemaphoreSlim(1, 1));
    var pair = new SyncPair { Id = "pushtest", LocalPath = localOverride, RemotePath = remoteOverride, Mode = SyncMode.OnDemand };

    Console.WriteLine("Enabling on-demand…");
    var count = await cloud.EnableAsync(account.Id, pair);
    Console.WriteLine($"  + enabled ({count} remote item(s)).");

    var newName = $"pushed-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
    var content = $"Pushed up from an on-demand folder at {DateTime.UtcNow:O}";
    File.WriteAllText(Path.Combine(localOverride, newName), content);
    Console.WriteLine($"\nAdded local file: {newName}\nPushing changes up…");

    var result = await cloud.SyncChangesAsync(account.Id, pair);
    Console.WriteLine($"  applied {result.Completed}, skipped {result.Skipped}, failed {result.Failures.Count}");
    foreach (var failure in result.Failures)
    {
        Console.WriteLine($"    FAIL {failure.Operation.Kind} {failure.Operation.RelativePath}: {failure.Error}");
    }

    cloud.Disable(pair.Id);

    // Verify + clean up via a fresh client.
    await using var verify = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (verify is not null)
    {
        var snapshot = await new RemoteSnapshotBuilder(verify).CaptureAsync(remoteOverride).ConfigureAwait(false);
        var found = snapshot?.Entries.Any(e => e.Name == newName) ?? false;
        Console.WriteLine($"\n  Remote now contains \"{newName}\": {(found ? "YES" : "NO")}");

        var node = await verify.ResolvePathAsync($"{remoteOverride.TrimEnd('/')}/{newName}").ConfigureAwait(false);
        if (node is not null)
        {
            await verify.TrashAsync(node).ConfigureAwait(false);
            Console.WriteLine("  cleaned up the remote test file.");
        }
    }

    engine.UnregisterSyncRoot(localOverride);
    new JsonSyncStateStore(paths).Clear(pair.Id);
    return result.AllSucceeded ? 0 : 1;
}

// Phase 5: automatic background sync. Enable on-demand, start the debounced FolderWatcher, drop a new
// local file, and verify the watcher pushes it up on its own (no manual SyncChangesAsync call). Then
// verify it reached Drive and clean up. `--watchtest <localFolder> <remotePath>`.
static async Task<int> WatchTestAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --watchtest <localFolder> <remotePath>");
        return 1;
    }

    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured.");
        return 1;
    }

    Console.WriteLine($"PAWS - auto-sync watch test\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var engine = new CloudFilterPlaceholderEngine();
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new SemaphoreSlim(1, 1));
    var pair = new SyncPair { Id = "watchtest", LocalPath = localOverride, RemotePath = remoteOverride, Mode = SyncMode.OnDemand };

    // Fires when an auto-sync run actually pushed something up.
    var pushed = new TaskCompletionSource<SyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    cloud.AutoSyncStarted += id => Console.WriteLine($"  [auto] change detected — syncing…");
    cloud.AutoSyncCompleted += e =>
    {
        if (!e.Succeeded)
        {
            Console.WriteLine($"  [auto] error: {e.Error!.Message}");
        }
        else
        {
            Console.WriteLine($"  [auto] done — pushed {e.Result!.Completed}, total {e.Result.Total}");
            if (e.Result.Total > 0)
            {
                pushed.TrySetResult(e.Result);
            }
        }
    };

    Console.WriteLine("Enabling on-demand…");
    var count = await cloud.EnableAsync(account.Id, pair);
    Console.WriteLine($"  + enabled ({count} remote item(s)).");

    cloud.StartAutoSync(account.Id, pair);
    Console.WriteLine("Auto-sync watcher started (debounced). Adding a local file…");

    var newName = $"watched-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
    File.WriteAllText(Path.Combine(localOverride, newName), $"Auto-pushed by the watcher at {DateTime.UtcNow:O}");
    Console.WriteLine($"  wrote {newName} — waiting for the watcher to push it (≤30s)…\n");

    var finished = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    var timedOut = finished != pushed.Task;
    if (timedOut)
    {
        Console.WriteLine("  x Timed out — the watcher did not push the change.");
    }

    cloud.StopAutoSync(pair.Id);
    cloud.Disable(pair.Id);

    // Verify + clean up via a fresh client.
    var ok = !timedOut;
    await using var verify = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (verify is not null)
    {
        var snapshot = await new RemoteSnapshotBuilder(verify).CaptureAsync(remoteOverride).ConfigureAwait(false);
        var found = snapshot?.Entries.Any(e => e.Name == newName) ?? false;
        Console.WriteLine($"  Remote now contains \"{newName}\": {(found ? "YES" : "NO")}");
        ok = ok && found;

        var node = await verify.ResolvePathAsync($"{remoteOverride.TrimEnd('/')}/{newName}").ConfigureAwait(false);
        if (node is not null)
        {
            await verify.TrashAsync(node).ConfigureAwait(false);
            Console.WriteLine("  cleaned up the remote test file.");
        }
    }

    engine.UnregisterSyncRoot(localOverride);
    new JsonSyncStateStore(paths).Clear(pair.Id);
    return ok ? 0 : 1;
}

// Phase 6: pull remote changes down. Enable on-demand (baseline), then SIMULATE a remote-side change via
// a second client (upload a new file to Drive), pull it down as a placeholder, verify it appeared locally;
// then trash it on Drive, pull again, and verify the local placeholder was removed. `--pulltest <local> <remote>`.
static async Task<int> PullTestAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --pulltest <localFolder> <remotePath>");
        return 1;
    }

    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured.");
        return 1;
    }

    Console.WriteLine($"PAWS - on-demand pull test\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var engine = new CloudFilterPlaceholderEngine();
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new SemaphoreSlim(1, 1));
    var pair = new SyncPair { Id = "pulltest", LocalPath = localOverride, RemotePath = remoteOverride, Mode = SyncMode.OnDemand };

    Console.WriteLine("Enabling on-demand (sets the baseline)…");
    var count = await cloud.EnableAsync(account.Id, pair);
    Console.WriteLine($"  + enabled ({count} remote item(s)).");

    var newName = $"pulled-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
    var localFile = Path.Combine(localOverride, newName);

    var ok = true;

    // Simulate "another machine changed Drive": upload a new remote file via a separate client.
    await using (var author = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        if (author is null)
        {
            return 1;
        }

        var folder = await author.ResolvePathAsync(remoteOverride).ConfigureAwait(false);
        if (folder is null)
        {
            Console.WriteLine($"  x Remote folder not found: {remoteOverride}");
            return 1;
        }

        using var content = new MemoryStream(Encoding.UTF8.GetBytes($"Created directly on Drive at {DateTime.UtcNow:O}"));
        await author.UploadAsync(folder, newName, content).ConfigureAwait(false);
        Console.WriteLine($"\nUploaded a new remote file directly to Drive: {newName}");
    }

    Console.WriteLine("Pulling remote changes down…");
    var pull = await cloud.PullChangesAsync(account.Id, pair);
    Console.WriteLine($"  created {pull.Created}, updated {pull.Updated}, deleted {pull.Deleted}");

    var appeared = File.Exists(localFile);
    Console.WriteLine($"  Local placeholder \"{newName}\" exists: {(appeared ? "YES" : "NO")}");
    if (appeared)
    {
        var attrs = File.GetAttributes(localFile);
        var onDemand = (attrs & (FileAttributes)0x00400000) != 0; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
        var onDisk = new FileInfo(localFile).Length;
        Console.WriteLine($"    on-demand placeholder: {(onDemand ? "YES" : "NO")}, logical size {onDisk} B");
    }
    ok = ok && pull.Created >= 1 && appeared;

    // Delete it on Drive and pull again — the placeholder should disappear. PullChangesAsync resumes a
    // fresh session so it sees the deletion; allow a window for Proton's eventual consistency to surface it.
    Console.WriteLine("\nTrashing the file on Drive, then pulling until the deletion surfaces…");
    await using (var author = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        var node = author is null ? null : await author.ResolvePathAsync($"{remoteOverride.TrimEnd('/')}/{newName}").ConfigureAwait(false);
        if (node is not null)
        {
            await author!.TrashAsync(node).ConfigureAwait(false);
            Console.WriteLine("  trashed on Drive.");
        }
    }

    var removed = false;
    for (var attempt = 1; attempt <= 12 && !removed; attempt++)
    {
        var pull2 = await cloud.PullChangesAsync(account.Id, pair);
        removed = !File.Exists(localFile);
        Console.WriteLine($"  attempt {attempt,2}: deleted {pull2.Deleted}; placeholder gone: {(removed ? "YES" : "no")}");
        if (!removed)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }
    ok = ok && removed;

    if (File.Exists(localFile))
    {
        try { File.Delete(localFile); } catch { /* ignore */ }
    }

    cloud.Disable(pair.Id);
    engine.UnregisterSyncRoot(localOverride);
    new JsonSyncStateStore(paths).Clear(pair.Id);

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS (create + delete pull verified)" : "FAIL")}");
    return ok ? 0 : 1;
}

// Phase 9: automatic FULL two-way sync. Start FullSyncService auto on a full-sync pair, drop a local file,
// and verify the watcher triggers a full sync that uploads it to Drive. `--fullautotest <local> <remote>`.
static async Task<int> FullAutoTestAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --fullautotest <localFolder> <remotePath>");
        return 1;
    }

    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured.");
        return 1;
    }

    Console.WriteLine($"PAWS - full-sync auto test\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var stateStore = new JsonSyncStateStore(paths);
    var engine = new SyncEngine(new ProtonDriveClientFactory(new DpapiSecretStore(paths)), stateStore, new SemaphoreSlim(1, 1));
    using var full = new FullSyncService(engine);
    var pair = new SyncPair { Id = "fullautotest", LocalPath = localOverride, RemotePath = remoteOverride, Mode = SyncMode.FullSync };

    var done = new TaskCompletionSource<FullSyncEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    full.SyncStarted += _ => Console.WriteLine("  [auto] full sync started…");
    full.SyncCompleted += e =>
    {
        if (e.Error is not null)
        {
            Console.WriteLine($"  [auto] error: {e.Error.Message}");
        }
        else if (e.NeedsReview)
        {
            Console.WriteLine($"  [auto] held for review: {e.PendingDeletes} deletions");
        }
        else
        {
            Console.WriteLine($"  [auto] done: {e.Result!.Completed} applied, {e.Result.Skipped} skipped, {e.Result.Failures.Count} failed");
        }

        done.TrySetResult(e);
    };

    // Start auto FIRST so the watcher is live, then create the local file so it's actually caught.
    full.StartAutoSync(account.Id, pair);
    await Task.Delay(TimeSpan.FromMilliseconds(500));

    var newName = $"fullauto-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
    File.WriteAllText(Path.Combine(localOverride, newName), $"Auto full-synced at {DateTime.UtcNow:O}");
    Console.WriteLine($"  wrote {newName} — waiting for the watcher to trigger a full sync (≤40s)…\n");

    var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(40)));
    var timedOut = finished != done.Task;
    full.StopAutoSync(pair.Id);
    if (timedOut)
    {
        Console.WriteLine("  x Timed out — the watcher did not trigger a sync.");
    }

    var ok = !timedOut;
    await using (var verify = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        if (verify is not null)
        {
            var snapshot = await new RemoteSnapshotBuilder(verify).CaptureAsync(remoteOverride).ConfigureAwait(false);
            var found = snapshot?.Entries.Any(e => e.Name == newName) ?? false;
            Console.WriteLine($"  Remote now contains \"{newName}\": {(found ? "YES" : "NO")}");
            ok = ok && found;

            var node = await verify.ResolvePathAsync($"{remoteOverride.TrimEnd('/')}/{newName}").ConfigureAwait(false);
            if (node is not null)
            {
                await verify.TrashAsync(node).ConfigureAwait(false);
                Console.WriteLine("  cleaned up the remote test file.");
            }
        }
    }

    stateStore.Clear(pair.Id);
    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// Diagnostic: does a LONG-LIVED client (the one a running app keeps) ever notice a remote deletion, or
// does it stay blind until process restart? Enables on-demand, uploads + pulls a file, trashes it on
// Drive, then polls PullChangesAsync on the SAME CloudSyncService every 15s for up to 5 minutes, printing
// when (if ever) the deletion is detected. Settles server-eventual-consistency vs. permanent client cache.
static async Task<int> DeleteConsistencyTestAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --deltest <localFolder> <remotePath>");
        return 1;
    }

    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured.");
        return 1;
    }

    Console.WriteLine($"PAWS - delete-consistency diagnostic\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var engine = new CloudFilterPlaceholderEngine();
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new SemaphoreSlim(1, 1));
    var pair = new SyncPair { Id = "deltest", LocalPath = localOverride, RemotePath = remoteOverride, Mode = SyncMode.OnDemand };

    await cloud.EnableAsync(account.Id, pair);
    var newName = $"deltest-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
    var localFile = Path.Combine(localOverride, newName);

    await using (var author = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        var folder = author is null ? null : await author.ResolvePathAsync(remoteOverride).ConfigureAwait(false);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("delete-consistency probe"));
        if (folder is not null)
        {
            await author!.UploadAsync(folder, newName, content).ConfigureAwait(false);
        }
    }

    await cloud.PullChangesAsync(account.Id, pair); // bring it down as a placeholder
    Console.WriteLine($"  placeholder present after create-pull: {File.Exists(localFile)}");

    var trashUtc = DateTime.UtcNow;
    await using (var author = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        var node = author is null ? null : await author.ResolvePathAsync($"{remoteOverride.TrimEnd('/')}/{newName}").ConfigureAwait(false);
        if (node is not null)
        {
            await author!.TrashAsync(node).ConfigureAwait(false);
        }
    }
    Console.WriteLine($"  trashed on Drive at {trashUtc:HH:mm:ss}Z — polling the long-lived client every 15s (max 5 min)…\n");

    var detected = false;
    for (var attempt = 1; attempt <= 20 && !detected; attempt++)
    {
        var pull = await cloud.PullChangesAsync(account.Id, pair);
        detected = !File.Exists(localFile);
        var elapsed = (DateTime.UtcNow - trashUtc).TotalSeconds;
        Console.WriteLine($"  +{elapsed,5:F0}s  attempt {attempt,2}: deleted {pull.Deleted}; gone: {(detected ? "YES" : "no")}");
        if (!detected)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
    }

    Console.WriteLine(detected
        ? "\n  => Long-lived client DID detect the deletion (server eventual consistency; periodic pull suffices)."
        : "\n  => Long-lived client did NOT detect it within 5 min (needs cache invalidation or restart).");

    if (File.Exists(localFile))
    {
        try { File.Delete(localFile); } catch { /* ignore */ }
    }

    cloud.Disable(pair.Id);
    engine.UnregisterSyncRoot(localOverride);
    new JsonSyncStateStore(paths).Clear(pair.Id);
    return 0;
}

// Diagnostic: does a FRESH client created per poll (new session each time, in the SAME process) see a
// remote deletion, or is the staleness process-wide (only a new process clears it)? Uploads a file,
// trashes it, then polls by creating a brand-new client each iteration and snapshotting. If fresh
// in-process clients detect it (like a new process does) → per-session cache, an evict-per-pull fixes it.
// If they don't → the cache is process-wide and only a restart helps.
static async Task<int> FreshClientPollTestAsync(string remotePath)
{
    Console.WriteLine($"PAWS - fresh-client poll diagnostic on \"{remotePath}\"\n");

    var newName = $"freshpoll-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";

    await using (var author = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        if (author is null)
        {
            return 1;
        }

        var folder = await author.ResolvePathAsync(remotePath).ConfigureAwait(false);
        if (folder is null)
        {
            Console.WriteLine($"  x Remote folder not found: {remotePath}");
            return 1;
        }

        using var content = new MemoryStream(Encoding.UTF8.GetBytes("fresh-client poll probe"));
        await author.UploadAsync(folder, newName, content).ConfigureAwait(false);
        Console.WriteLine($"  uploaded {newName}");
    }

    var trashUtc = DateTime.UtcNow;
    await using (var author = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        var node = author is null ? null : await author.ResolvePathAsync($"{remotePath.TrimEnd('/')}/{newName}").ConfigureAwait(false);
        if (node is not null)
        {
            await author!.TrashAsync(node).ConfigureAwait(false);
        }
    }
    Console.WriteLine($"  trashed at {trashUtc:HH:mm:ss}Z — polling with a NEW client each time (15s, max 5 min)…\n");

    var detected = false;
    for (var attempt = 1; attempt <= 20 && !detected; attempt++)
    {
        await using (var probe = await ConnectFromStoredAsync().ConfigureAwait(false))
        {
            var snapshot = probe is null ? null : await new RemoteSnapshotBuilder(probe).CaptureAsync(remotePath).ConfigureAwait(false);
            var present = snapshot?.Entries.Any(e => e.Name == newName) ?? true;
            detected = !present;
            var elapsed = (DateTime.UtcNow - trashUtc).TotalSeconds;
            Console.WriteLine($"  +{elapsed,5:F0}s  attempt {attempt,2}: file present: {(present ? "yes" : "NO (gone)")}");
        }

        if (!detected)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
    }

    Console.WriteLine(detected
        ? "\n  => A FRESH in-process client DID detect the deletion → per-session cache; evict-per-pull would fix it."
        : "\n  => Even fresh in-process clients did NOT detect it within 5 min → process-wide cache; only a restart clears it.");
    return 0;
}

// Tears down a Cloud Filter sync root registration (cleanup for testing).
static int Unregister(string? localFolder)
{
    if (localFolder is null)
    {
        Console.WriteLine("  x Usage: --unregister <localFolder>");
        return 1;
    }

    try
    {
        new CloudFilterPlaceholderEngine().UnregisterSyncRoot(localFolder);
        Console.WriteLine($"  + Unregistered sync root: {localFolder}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Unregister failed: {ex.Message}");
        return 1;
    }
}

// Offline: proves the native proton_crypto library loads and runs, without a Proton account.
static int CryptoCheck()
{
    try
    {
        Console.WriteLine("Generating a throwaway PGP key via the native proton_crypto library…");
        var fingerprint = ProtonCryptoSelfTest.GenerateKeyFingerprint();
        Console.WriteLine($"OK - native crypto works. Generated key fingerprint: {fingerprint}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED to load/run native crypto:\n{ex}");
        return 1;
    }
}

// Browser/session-fork login -> resume the SDK session -> connected Drive client. Null on failure.
static async Task<IProtonDriveClient?> SignInAndConnectAsync()
{
    var web = new WebProtonAuthenticator();
    var auth = await web.SignInAsync(challenge =>
    {
        Console.WriteLine($"Opening: {challenge.Url}\n");
        try { Process.Start(new ProcessStartInfo { FileName = challenge.Url, UseShellExecute = true }); }
        catch { Console.WriteLine("(Could not open a browser automatically - paste the URL above.)"); }
        Console.WriteLine("Waiting for you to finish signing in on the website…");
        return Task.CompletedTask;
    }).ConfigureAwait(false);

    if (!auth.IsSuccess)
    {
        Console.WriteLine($"  x sign-in failed: {auth.Status}: {auth.Message}");
        return null;
    }

    Console.WriteLine($"  + Signed in as {auth.Session!.Username}\n");

    var drive = new ProtonDriveClientAdapter(await ProtonSessionConnector.ResumeAsync(auth.Session).ConfigureAwait(false));
    await drive.ConnectAsync().ConfigureAwait(false);
    return drive;
}

// End-to-end: browser login -> resume SDK session -> Drive list (and optional upload/download/trash).
static async Task<int> DriveAsync(bool doWrite)
{
    Console.WriteLine("PAWS - Proton Drive test\n");

    await using var drive = await SignInAndConnectAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    var root = await drive.GetRootAsync().ConfigureAwait(false);
    Console.WriteLine($"My files root: uid={root.Uid}\n");

    // 3) List the root folder.
    Console.WriteLine("Children of root:");
    var count = 0;
    await foreach (var child in drive.ListChildrenAsync(root).ConfigureAwait(false))
    {
        var kind = child.IsFolder ? "DIR " : "file";
        var size = child.Size is { } s ? $"{s,12:N0} B" : new string(' ', 14);
        Console.WriteLine($"  [{kind}] {size}  {child.Name}");
        count++;
    }

    Console.WriteLine($"\n  ({count} item(s))");

    if (!doWrite)
    {
        Console.WriteLine("\nPass --write to also run an upload/download/trash round-trip.");
        return 0;
    }

    // 4) Round-trip: upload a small file, download it back, verify bytes, then trash it.
    Console.WriteLine("\n— round-trip —");
    var payload = $"PAWS round-trip {DateTime.UtcNow:O}";
    var name = $"paws-roundtrip-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";

    using var upload = new MemoryStream(Encoding.UTF8.GetBytes(payload));
    var uploaded = await drive.UploadAsync(root, name, upload, DateTimeOffset.UtcNow).ConfigureAwait(false);
    Console.WriteLine($"  uploaded   : {uploaded.Name}  (uid={uploaded.Uid})");

    using var download = new MemoryStream();
    await drive.DownloadAsync(uploaded, download).ConfigureAwait(false);
    var roundTripped = Encoding.UTF8.GetString(download.ToArray());

    var ok = string.Equals(roundTripped, payload, StringComparison.Ordinal);
    Console.WriteLine($"  downloaded : {download.Length} B, content match = {(ok ? "YES" : "NO")}");

    try
    {
        await drive.TrashAsync(uploaded).ConfigureAwait(false);
        Console.WriteLine("  trashed    : test file moved to Drive trash");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  trash FAILED: {ex.Message}");
    }

    return ok ? 0 : 1;
}

// Phase 2: capture and print the full remote subtree snapshot under a path (default "/").
static async Task<int> SnapshotAsync(string remotePath)
{
    Console.WriteLine($"PAWS - remote snapshot of \"{remotePath}\"\n");

    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    Console.WriteLine($"Walking {remotePath} …\n");

    var snapshot = await new RemoteSnapshotBuilder(drive).CaptureAsync(remotePath).ConfigureAwait(false);
    if (snapshot is null)
    {
        Console.WriteLine($"  x \"{remotePath}\" did not resolve to a folder.");
        return 1;
    }

    foreach (var entry in snapshot.Entries)
    {
        var depth = entry.RelativePath.AsSpan().Count('/');
        var indent = new string(' ', depth * 2);
        var marker = entry.IsFolder ? "[DIR ]" : "file  ";
        var size = entry.IsFile ? $"   ({FormatSize(entry.Size ?? 0)})" : string.Empty;
        Console.WriteLine($"  {marker} {indent}{entry.Name}{size}");
    }

    Console.WriteLine($"\n  {snapshot.FolderCount} folder(s), {snapshot.FileCount} file(s), {FormatSize(snapshot.TotalFileBytes)} total.");
    Console.WriteLine($"  captured {snapshot.CapturedUtc:u}");
    return 0;
}

// Phase 3: register a Cloud Filter sync root and mirror a remote tree into it as on-demand
// placeholders. `--placeholders <localFolder> <remotePath>`. (Hydration-on-open is a later step.)
static async Task<int> PlaceholdersAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --placeholders <localFolder> <remotePath>");
        return 1;
    }

    Console.WriteLine($"PAWS - cloud placeholders\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var engine = new CloudFilterPlaceholderEngine();
    if (!engine.IsSupported)
    {
        Console.WriteLine("  x Cloud Filter API not supported on this OS.");
        return 1;
    }

    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    var snapshot = await new RemoteSnapshotBuilder(drive).CaptureAsync(remoteOverride).ConfigureAwait(false);
    if (snapshot is null)
    {
        Console.WriteLine($"  x Remote path is not a folder: {remoteOverride}");
        return 1;
    }

    Console.WriteLine($"Remote: {snapshot.FolderCount} folder(s), {snapshot.FileCount} file(s).\n");

    try
    {
        Console.WriteLine("Registering sync root…");
        engine.RegisterSyncRoot(new SyncRootInfo(localOverride, "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64", "PAWS", "1.0.0.0"));
        Console.WriteLine("  + registered");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Register failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine("Creating placeholders…");
    var result = engine.CreatePlaceholders(localOverride, snapshot);
    Console.WriteLine($"  created {result.Created}, skipped {result.Skipped}, errors {result.Errors.Count}");
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"    {error}");
    }

    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    Console.WriteLine("\nLocal entries (on-demand = no bytes on disk until opened):");
    foreach (var path in Directory.EnumerateFileSystemEntries(localOverride, "*", SearchOption.AllDirectories).Order())
    {
        var attr = File.GetAttributes(path);
        var isDir = (attr & FileAttributes.Directory) != 0;
        var onDemand = (attr & (RecallOnDataAccess | FileAttributes.Offline)) != 0;
        var size = isDir ? string.Empty : $"  ({new FileInfo(path).Length} B logical)";
        Console.WriteLine($"  {(isDir ? "DIR " : "file")} {(onDemand ? "[on-demand]" : "[local]    ")} {Path.GetRelativePath(localOverride, path)}{size}");
    }

    return result.Errors.Count == 0 ? 0 : 1;
}

// Phase 4b: REAL sync (moves files). Uses the first configured pair, or `--sync <local> <remote>`.
static async Task<int> SyncAsync(string? localOverride, string? remoteOverride)
{
    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured. Add one in the PAWS app first.");
        return 1;
    }

    SyncPair pair;
    if (localOverride is not null && remoteOverride is not null)
    {
        pair = new SyncPair { Id = "harnesstest", LocalPath = localOverride, RemotePath = remoteOverride, Mode = SyncMode.FullSync };
        Console.WriteLine("  (using explicit local/remote override — stable test pair 'harnesstest')");
    }
    else
    {
        var configured = account.SyncPairs.FirstOrDefault();
        if (configured is null)
        {
            Console.WriteLine("  x No folder configured. Pass: --sync <localPath> <remotePath>");
            return 1;
        }

        pair = configured;
    }

    Console.WriteLine($"PAWS - sync\n  local  : {pair.LocalPath}\n  remote : {pair.RemotePath}\n");

    var engine = new SyncEngine(new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new SemaphoreSlim(1, 1));

    SyncPlan plan;
    try
    {
        plan = await engine.PlanAsync(account.Id, pair);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Plan failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"Plan: {plan.Operations.Count} operation(s)");
    foreach (var op in plan.Operations)
    {
        Console.WriteLine($"  {op.Kind,-18} {op.RelativePath}{(op.IsFolder ? "/" : string.Empty)}");
    }

    if (plan.Operations.Count == 0)
    {
        Console.WriteLine("\nAlready in sync — nothing to do.");
        return 0;
    }

    Console.WriteLine("\nApplying…");
    var progress = new Progress<SyncProgress>(p => Console.WriteLine($"  [{p.Completed + 1}/{p.Total}] {p.Current.Kind} {p.Current.RelativePath}"));
    var result = await engine.ApplyAsync(account.Id, plan, progress);

    Console.WriteLine($"\nDone: {result.Completed} applied, {result.Skipped} skipped (conflicts), {result.Failures.Count} failed.");
    foreach (var f in result.Failures)
    {
        Console.WriteLine($"  FAIL {f.Operation.Kind} {f.Operation.RelativePath}: {f.Error}");
    }

    return result.AllSucceeded ? 0 : 1;
}

// Phase 4: offline self-test of the reconciler diff — fabricated remote/local/last-known snapshots
// exercising every branch, asserting the resulting operation per path. No network, no config.
static int ReconcileSelfTest()
{
    Console.WriteLine("PAWS - reconciler self-test\n");

    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var t1 = t0.AddHours(1);

    static RemoteEntry RFile(string path, string rev, long size) => new()
    { RelativePath = path, Name = path, IsFolder = false, Size = size, Uid = "node-" + path, RevisionUid = rev };
    static RemoteEntry RDir(string path) => new() { RelativePath = path, Name = path, IsFolder = true, Uid = "node-" + path };
    static LocalEntry LFile(string path, long size, DateTimeOffset m) => new()
    { RelativePath = path, Name = path, IsFolder = false, Size = size, ModifiedUtc = m };
    static SyncStateEntry SFile(string path, string rev, long size, DateTimeOffset m) => new()
    { RelativePath = path, IsFolder = false, RemoteUid = "node-" + path, RemoteRevisionUid = rev, Size = size, LocalModifiedUtc = m };

    var remote = new RemoteSnapshot
    {
        RootPath = "/",
        CapturedUtc = DateTimeOffset.UtcNow,
        Entries =
        [
            RFile("shared.txt", "rev-shared-1", 10),
            RFile("remote-changed.txt", "rev-rc-2", 22),
            RFile("local-changed.txt", "rev-lc-1", 30),
            RFile("both-changed.txt", "rev-bc-2", 40),
            RFile("deleted-local.txt", "rev-dl-1", 50),
            RFile("new-remote.txt", "rev-nr-1", 7),
            RDir("newdir"),
            RFile("adopt-same.txt", "rev-as-1", 100),
            RFile("nohist-conflict.txt", "rev-nc-1", 100),
        ],
    };

    var local = new LocalSnapshot
    {
        RootPath = @"C:\local",
        CapturedUtc = DateTimeOffset.UtcNow,
        Entries =
        [
            LFile("shared.txt", 10, t0),
            LFile("remote-changed.txt", 20, t0),
            LFile("local-changed.txt", 33, t1),
            LFile("both-changed.txt", 44, t1),
            LFile("deleted-remote.txt", 60, t0),
            LFile("new-local.txt", 5, t1),
            LFile("adopt-same.txt", 100, t0),
            LFile("nohist-conflict.txt", 200, t0),
        ],
    };

    var state = new SyncState
    {
        PairId = "p1",
        Entries =
        [
            SFile("shared.txt", "rev-shared-1", 10, t0),
            SFile("remote-changed.txt", "rev-rc-1", 20, t0),
            SFile("local-changed.txt", "rev-lc-1", 30, t0),
            SFile("both-changed.txt", "rev-bc-1", 40, t0),
            SFile("deleted-local.txt", "rev-dl-1", 50, t0),
            SFile("deleted-remote.txt", "rev-dr-1", 60, t0),
        ],
    };

    var ops = new Reconciler().Reconcile(remote, local, state);
    var actual = ops.ToDictionary(o => o.RelativePath, o => (SyncOperationKind?)o.Kind, StringComparer.Ordinal);

    var expected = new Dictionary<string, SyncOperationKind?>(StringComparer.Ordinal)
    {
        ["shared.txt"] = null,                                       // unchanged both sides
        ["remote-changed.txt"] = SyncOperationKind.DownloadFile,     // remote newer
        ["local-changed.txt"] = SyncOperationKind.UploadFile,        // local newer
        ["both-changed.txt"] = SyncOperationKind.Conflict,           // both changed
        ["deleted-remote.txt"] = SyncOperationKind.DeleteLocal,      // gone on remote
        ["deleted-local.txt"] = SyncOperationKind.DeleteRemote,      // gone on local
        ["new-remote.txt"] = SyncOperationKind.DownloadFile,         // new on remote
        ["newdir"] = SyncOperationKind.CreateLocalFolder,            // new folder on remote
        ["new-local.txt"] = SyncOperationKind.UploadFile,            // new on local
        ["adopt-same.txt"] = null,                                   // no history, same size -> adopt
        ["nohist-conflict.txt"] = SyncOperationKind.Conflict,        // no history, different size
    };

    var allOk = true;
    foreach (var (path, exp) in expected)
    {
        actual.TryGetValue(path, out var act);
        var ok = act == exp;
        allOk &= ok;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {path,-22} expected {exp?.ToString() ?? "no-op",-16} got {act?.ToString() ?? "no-op"}");
    }

    foreach (var op in ops.Where(o => !expected.ContainsKey(o.RelativePath)))
    {
        Console.WriteLine($"  [FAIL] unexpected op: {op.Kind} {op.RelativePath}");
        allOk = false;
    }

    Console.WriteLine();
    Console.WriteLine(allOk ? "RECONCILER SELF-TEST PASSED" : "RECONCILER SELF-TEST FAILED");
    return allOk ? 0 : 1;
}

// Phase 4: dry-run sync preview for the first configured pair — capture remote + local, reconcile
// against an empty (first-sync) state, and print the planned operations. Moves no files.
static async Task<int> PlanAsync()
{
    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    var pair = account?.SyncPairs.FirstOrDefault();
    if (account is null || pair is null)
    {
        Console.WriteLine("  x No account+folder configured. Add one in the PAWS app first.");
        return 1;
    }

    Console.WriteLine("PAWS - sync preview (dry run)\n");
    Console.WriteLine($"  local  : {pair.LocalPath}");
    Console.WriteLine($"  remote : {pair.RemotePath}\n");

    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    var remote = await new RemoteSnapshotBuilder(drive).CaptureAsync(pair.RemotePath).ConfigureAwait(false);
    if (remote is null)
    {
        Console.WriteLine($"  x Remote path is not a folder: {pair.RemotePath}");
        return 1;
    }

    var local = new LocalSnapshotBuilder().Capture(pair.LocalPath);
    if (local is null)
    {
        Console.WriteLine($"  x Local folder not found: {pair.LocalPath}");
        return 1;
    }

    Console.WriteLine($"  remote: {remote.FolderCount} folder(s), {remote.FileCount} file(s)");
    Console.WriteLine($"  local : {local.FolderCount} folder(s), {local.FileCount} file(s)\n");

    var ops = new Reconciler().Reconcile(remote, local, SyncState.Empty(pair.Id));

    if (ops.Count == 0)
    {
        Console.WriteLine("Already in sync — nothing to do.");
        return 0;
    }

    Console.WriteLine($"Planned operations ({ops.Count}):");
    foreach (var op in ops)
    {
        Console.WriteLine($"  {op.Kind,-18} {op.RelativePath}{(op.IsFolder ? "/" : string.Empty)}   — {op.Reason}");
    }

    Console.WriteLine("\nSummary:");
    foreach (var group in ops.GroupBy(o => o.Kind))
    {
        Console.WriteLine($"  {group.Count(),4}  {group.Key}");
    }

    return 0;
}

// Trash a single node by path (stored creds). Decisive test of the trash result handling + cleanup.
static async Task<int> TrashPathAsync(string remotePath)
{
    if (remotePath is "/" or "")
    {
        Console.WriteLine("  x Provide a path to trash, e.g.: --trash paws-roundtrip-….txt");
        return 1;
    }

    Console.WriteLine($"PAWS - trash \"{remotePath}\"\n");

    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    var node = await drive.ResolvePathAsync(remotePath).ConfigureAwait(false);
    if (node is null)
    {
        Console.WriteLine($"  x Not found: {remotePath}");
        return 1;
    }

    Console.WriteLine($"  Found {(node.IsFolder ? "folder" : "file")}: {node.Name}  (uid={node.Uid})");

    try
    {
        await drive.TrashAsync(node).ConfigureAwait(false);
        Console.WriteLine("  + Trashed successfully.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Trash FAILED: {ex.Message}");
        return 1;
    }
}

// Connect using an already-configured app account's stored session (no browser login). Exercises the
// real resume + token-rotation path; null if nothing is configured or the session can't be resumed.
static async Task<IProtonDriveClient?> ConnectFromStoredAsync()
{
    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured. Add one in the PAWS app (or: PAWS.Setup --weblogin).");
        return null;
    }

    Console.WriteLine($"  Using stored account: {account.Label} [{account.Id[..8]}]\n");

    var factory = new ProtonDriveClientFactory(new DpapiSecretStore(paths));
    try
    {
        return await factory.CreateAsync(account.Id).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Could not resume the stored session: {ex.Message}");
        Console.WriteLine("    If it mentions a token/session, open the app and click \"Sign in again\", then retry.");
        return null;
    }
}

static string FormatSize(long bytes)
{
    string[] units = { "B", "KB", "MB", "GB", "TB" };
    double size = bytes;
    var unit = 0;
    while (size >= 1024 && unit < units.Length - 1)
    {
        size /= 1024;
        unit++;
    }

    return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
}
