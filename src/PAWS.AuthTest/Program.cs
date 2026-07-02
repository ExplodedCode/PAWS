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
    "--lazytest" or "lazytest" => await LazyTestAsync(localArg, remoteArg),
    "--dirtest" or "dirtest" => DirTest(localArg, remoteArg),
    "--removetest" or "removetest" => RemoveTest(localArg),
    "--popfailtest" or "popfailtest" => PopFailTest(localArg),
    "--conflicttest" or "conflicttest" => ConflictPlanSelfTest(),
    "--weburl" or "weburl" => await WebUrlAsync(pathArg),
    "--throttletest" or "throttletest" => await ThrottleTestAsync(),
    "--pausetest" or "pausetest" => await PauseTestAsync(localArg, remoteArg),
    "--dehydratetest" or "dehydratetest" => await DehydrateTestAsync(localArg, remoteArg),
    "--shellreg" or "shellreg" => await ShellRegCheckAsync(localArg),
    "--shellquery" or "shellquery" => await ShellQueryAsync(localArg),
    "--shellfix" or "shellfix" => await ShellFixAsync(localArg),
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

    using var connection = engine.Connect(localOverride, MakeTestFetchChildren(drive, remoteOverride), async (identity, output, ct) =>
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
    using var connection = engine.Connect(localOverride, MakeTestFetchChildren(drive, remoteOverride), async (identity, output, ct) =>
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

// Phase: scalable (lazy) population + the critical push-safety invariant. Builds a remote test tree
// (root.txt + sub/{a.txt,b.txt}), enables on-demand (shallow), and verifies: (1) only the root is
// materialized (sub/ is a placeholder, its files are NOT); (2) a PUSH does NOT trash the un-browsed
// sub/ files (the data-loss guard); (3) browsing sub/ populates it. `--lazytest <local> <remote>`.
static async Task<int> LazyTestAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --lazytest <localFolder> <remotePath>");
        return 1;
    }

    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured.");
        return 1;
    }

    Console.WriteLine($"PAWS - lazy population + push-safety test\n  local  : {localOverride}\n  remote : {remoteOverride}\n");

    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    var testRoot = $"{remoteOverride.TrimEnd('/')}/_lazytest_{stamp}";

    // --- Build the remote test tree: _lazytest_x/{root.txt, sub/{a.txt, b.txt}} ---
    await using (var setup = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        if (setup is null)
        {
            return 1;
        }

        var parent = await setup.ResolvePathAsync(remoteOverride).ConfigureAwait(false);
        if (parent is null)
        {
            Console.WriteLine($"  x Remote folder not found: {remoteOverride}");
            return 1;
        }

        var testFolder = await setup.CreateFolderAsync(parent, $"_lazytest_{stamp}").ConfigureAwait(false);
        using (var c = new MemoryStream(Encoding.UTF8.GetBytes("root")))
        {
            await setup.UploadAsync(testFolder, "root.txt", c).ConfigureAwait(false);
        }

        var sub = await setup.CreateFolderAsync(testFolder, "sub").ConfigureAwait(false);
        using (var c = new MemoryStream(Encoding.UTF8.GetBytes("aaa")))
        {
            await setup.UploadAsync(sub, "a.txt", c).ConfigureAwait(false);
        }

        using (var c = new MemoryStream(Encoding.UTF8.GetBytes("bbb")))
        {
            await setup.UploadAsync(sub, "b.txt", c).ConfigureAwait(false);
        }
    }

    Console.WriteLine($"  built remote tree {testRoot}/  (root.txt, sub/a.txt, sub/b.txt)\n");

    var engine = new CloudFilterPlaceholderEngine();
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new JsonPopulatedFolderStore(paths), new SemaphoreSlim(1, 1));
    var pair = new SyncPair { Id = $"lazytest{stamp}", LocalPath = localOverride, RemotePath = testRoot, Mode = SyncMode.OnDemand };

    var ok = true;
    try
    {
        // 1. Shallow enable — only the root should materialize.
        var count = await cloud.EnableAsync(account.Id, pair);
        var rootTxt = File.Exists(Path.Combine(localOverride, "root.txt"));
        var subDir = Directory.Exists(Path.Combine(localOverride, "sub"));
        var subFileMaterialized = File.Exists(Path.Combine(localOverride, "sub", "a.txt"));
        Console.WriteLine($"  after shallow enable ({count} root item(s)): root.txt={rootTxt}, sub/ dir={subDir}, sub/a.txt materialized={subFileMaterialized}");
        ok = ok && rootTxt && subDir && !subFileMaterialized; // sub/ present but NOT populated

        // 2. CRITICAL: push must NOT trash the un-browsed sub/ files.
        Console.WriteLine("\n  pushing (sub/ is un-browsed — its remote files must survive)…");
        var push = await cloud.SyncChangesAsync(account.Id, pair);
        Console.WriteLine($"    push: {push.Completed} applied, {push.Failures.Count} failed");

        await using (var verify = await ConnectFromStoredAsync().ConfigureAwait(false))
        {
            var snap = verify is null ? null : await new RemoteSnapshotBuilder(verify).CaptureAsync(testRoot).ConfigureAwait(false);
            var names = snap?.Entries.Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal) ?? [];
            var survived = names.Contains("sub/a.txt") && names.Contains("sub/b.txt");
            Console.WriteLine($"    remote after push contains sub/a.txt & sub/b.txt: {(survived ? "YES (safe)" : "NO — DATA LOSS!")}");
            ok = ok && survived;
        }

        // 3. Browse sub/ from a SEPARATE process — this fires FETCH_PLACEHOLDERS and populates it lazily
        // (an in-process enumeration by the provider itself never triggers the callback).
        Console.WriteLine("\n  browsing sub/ from a separate process (should populate lazily)…");
        var (subOk, subEntries) = EnumerateInSeparateProcess(Path.Combine(localOverride, "sub"));
        Console.WriteLine($"    sub/ now contains: {(subOk ? string.Join(", ", subEntries) : subEntries[0])}");
        ok = ok && subOk && subEntries.Contains("a.txt") && subEntries.Contains("b.txt");

        // 4. CRITICAL: push AGAIN now that sub/ is populated. Its placeholder children are NOT in the saved
        // state (state was baselined at enable, root-only), and the delete-guard no longer shields sub/
        // (it's populated) — so the reconciler must ADOPT the just-browsed placeholders as no-ops, never
        // upload them again or trash their remote originals.
        Console.WriteLine("\n  pushing again (sub/ now populated — placeholders must adopt, not re-sync/trash)…");
        var push2 = await cloud.SyncChangesAsync(account.Id, pair);
        Console.WriteLine($"    push2: {push2.Completed} applied, {push2.Failures.Count} failed (expect 0 applied)");
        ok = ok && push2.Completed == 0 && push2.Failures.Count == 0;

        await using (var verify2 = await ConnectFromStoredAsync().ConfigureAwait(false))
        {
            var snap = verify2 is null ? null : await new RemoteSnapshotBuilder(verify2).CaptureAsync(testRoot).ConfigureAwait(false);
            var names = snap?.Entries.Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal) ?? [];
            var survived = names.Contains("sub/a.txt") && names.Contains("sub/b.txt");
            Console.WriteLine($"    remote after push2 still has sub/a.txt & sub/b.txt: {(survived ? "YES (safe)" : "NO — DATA LOSS!")}");
            ok = ok && survived;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Test error: {ex.GetType().Name}: {ex.Message}");
        ok = false;
    }
    finally
    {
        cloud.Disable(pair.Id);
        try { engine.UnregisterSyncRoot(localOverride); } catch { /* ignore */ }
        new JsonSyncStateStore(paths).Clear(pair.Id);
        new JsonPopulatedFolderStore(paths).Clear(pair.Id);

        await using var cleanup = await ConnectFromStoredAsync().ConfigureAwait(false);
        if (cleanup is not null)
        {
            var node = await cleanup.ResolvePathAsync(testRoot).ConfigureAwait(false);
            if (node is not null)
            {
                await cleanup.TrashAsync(node).ConfigureAwait(false);
                Console.WriteLine("\n  cleaned up the remote test tree.");
            }
        }
    }

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// Re-registers an existing sync root in place with current capabilities (verified AllowPinning) and
// LEAVES it registered — repairs a stale registration without needing the app. Only run while the PAWS
// app is NOT running (a live provider connection must not have the root yanked out from under it).
// `--shellfix <folder>`.
static async Task<int> ShellFixAsync(string? localOverride)
{
    if (localOverride is null || !Directory.Exists(localOverride))
    {
        Console.WriteLine("  x Usage: --shellfix <existingFolder>");
        return 1;
    }

    var engine = new CloudFilterPlaceholderEngine();
    engine.RegisterSyncRoot(new SyncRootInfo(localOverride, "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64", "PAWS", "1.0.0.0"));
    return await ShellQueryAsync(localOverride);
}

// READ-ONLY: prints what the shell has recorded for an existing sync root (no changes made).
// `--shellquery <folder>`.
static async Task<int> ShellQueryAsync(string? localOverride)
{
    if (localOverride is null)
    {
        Console.WriteLine("  x Usage: --shellquery <folder>");
        return 1;
    }

    try
    {
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(localOverride);
        var info = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
        Console.WriteLine($"  Id             : {info.Id}");
        Console.WriteLine($"  DisplayName    : {info.DisplayNameResource}");
        Console.WriteLine($"  AllowPinning   : {info.AllowPinning}");
        Console.WriteLine($"  Population     : {info.PopulationPolicy}");
        Console.WriteLine($"  Hydration      : {info.HydrationPolicy} / {info.HydrationPolicyModifier}");
        return info.AllowPinning ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Not a shell-registered sync root: {ex.Message}");
        return 1;
    }
}

// OFFLINE shell-registration check: registers a sync root, reads back what the shell recorded (Id +
// AllowPinning — the flag gating Explorer's "Always keep on this device"/"Free up space" menu), proves
// re-register updates in place, then unregisters. `--shellreg <folder>`.
static async Task<int> ShellRegCheckAsync(string? localOverride)
{
    if (localOverride is null)
    {
        Console.WriteLine("  x Usage: --shellreg <folder>");
        return 1;
    }

    var engine = new CloudFilterPlaceholderEngine();
    Directory.CreateDirectory(localOverride);
    var info = new SyncRootInfo(localOverride, "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64", "PAWS", "1.0.0.0");

    engine.RegisterSyncRoot(info);
    var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(localOverride);
    var registered = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
    Console.WriteLine($"  Id           : {registered.Id}");
    Console.WriteLine($"  AllowPinning : {registered.AllowPinning}");

    engine.RegisterSyncRoot(info); // must update in place (the path existing roots take on app relaunch)
    Console.WriteLine("  re-register  : OK");

    engine.UnregisterSyncRoot(localOverride);
    Console.WriteLine("  unregistered : OK");
    return registered.AllowPinning ? 0 : 1;
}

// End-to-end dehydration ("free up space") test: enable an on-demand pair, hydrate a file by reading it,
// then verify (1) FreeUpSpace dehydrates it back to cloud-only and it re-hydrates on read; (2) pinned
// files ("Always keep on this device") are skipped; (3) the age filter skips recently-used files;
// (4) a NEW local file, after a push, is finalized into an in-sync placeholder that can dehydrate AND
// re-hydrate with the correct (new-revision) content. Also reports whether the sync root got SHELL
// registration (= Explorer context menu available). `--dehydratetest <localFolder> <remotePath>`.
static async Task<int> DehydrateTestAsync(string? localOverride, string? remoteOverride)
{
    const int RecallOnDataAccess = 0x400000; // dehydrated (cloud-only) placeholder attribute
    const int Pinned = 0x80000;

    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --dehydratetest <localFolder> <remotePath>");
        return 1;
    }

    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured.");
        return 1;
    }

    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    var testRoot = $"{remoteOverride.TrimEnd('/')}/_dehydtest_{stamp}";
    var helloContent = $"dehydrate me {stamp}";

    await using (var setup = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        if (setup is null)
        {
            return 1;
        }

        var parent = await setup.ResolvePathAsync(remoteOverride).ConfigureAwait(false);
        if (parent is null)
        {
            Console.WriteLine($"  x Remote folder not found: {remoteOverride}");
            return 1;
        }

        var folder = await setup.CreateFolderAsync(parent, $"_dehydtest_{stamp}").ConfigureAwait(false);
        using var c = new MemoryStream(Encoding.UTF8.GetBytes(helloContent));
        await setup.UploadAsync(folder, "hello.txt", c).ConfigureAwait(false);
    }

    Console.WriteLine($"PAWS - dehydration test\n  local  : {localOverride}\n  remote : {testRoot}\n");

    Directory.CreateDirectory(localOverride);
    var engine = new CloudFilterPlaceholderEngine();
    await using var cloud = new CloudSyncService(
        engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths),
        new JsonPopulatedFolderStore(paths), new SemaphoreSlim(1, 1));
    var pair = new SyncPair { Id = $"dehydtest{stamp}", LocalPath = localOverride, RemotePath = testRoot, Mode = SyncMode.OnDemand };

    var ok = true;
    try
    {
        await cloud.EnableAsync(account.Id, pair);

        // Shell registration check (this is what puts "Free up space" in Explorer's context menu).
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(localOverride);
            var reg = Windows.Storage.Provider.StorageProviderSyncRootManager.GetSyncRootInformationForFolder(folder);
            Console.WriteLine($"  shell registration: YES — {reg.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  shell registration: NO ({ex.Message.Trim()}) — Explorer menu unavailable, Win32 fallback in use");
        }

        int Attrs(string p) => (int)File.GetAttributes(p);
        var hello = Path.Combine(localOverride, "hello.txt");

        // 1. Hydrate by reading, then FreeUpSpace should dehydrate it back to cloud-only.
        var read1 = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(hello));
        var hydrated = (Attrs(hello) & RecallOnDataAccess) == 0;
        Console.WriteLine($"  hydrate on read: content match={read1 == helloContent}, hydrated={hydrated}");
        ok = ok && read1 == helloContent && hydrated;

        var r1 = cloud.FreeUpSpace(pair);
        var offloaded = (Attrs(hello) & RecallOnDataAccess) != 0;
        Console.WriteLine($"  free up space: dehydrated={r1.Dehydrated}, now cloud-only={offloaded}  -> {(r1.Dehydrated >= 1 && offloaded ? "PASS" : "FAIL")}");
        ok = ok && r1.Dehydrated >= 1 && offloaded;

        // 2. Re-read still works (re-hydrates through the provider).
        var read2 = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(hello));
        Console.WriteLine($"  re-hydrate on read: content match={read2 == helloContent}  -> {(read2 == helloContent ? "PASS" : "FAIL")}");
        ok = ok && read2 == helloContent;

        // 3. Pinned files are skipped ("Always keep on this device").
        File.SetAttributes(hello, (FileAttributes)(Attrs(hello) | Pinned));
        var r2 = cloud.FreeUpSpace(pair);
        var stillHydrated = (Attrs(hello) & RecallOnDataAccess) == 0;
        Console.WriteLine($"  pinned skip: dehydrated={r2.Dehydrated}, still hydrated={stillHydrated}  -> {(r2.Dehydrated == 0 && stillHydrated ? "PASS" : "FAIL")}");
        ok = ok && r2.Dehydrated == 0 && stillHydrated;
        File.SetAttributes(hello, (FileAttributes)(Attrs(hello) & ~Pinned));

        // 4. Age filter: a freshly-used file is left alone.
        var r3 = cloud.FreeUpSpace(pair, TimeSpan.FromDays(1));
        Console.WriteLine($"  age filter (1 day): dehydrated={r3.Dehydrated}  -> {(r3.Dehydrated == 0 ? "PASS" : "FAIL")}");
        ok = ok && r3.Dehydrated == 0;

        // 5. New local file → push → finalize should make it a dehydratable in-sync placeholder whose
        // identity points at the uploaded revision (so it re-hydrates with the right content).
        var newContent = $"born local {stamp}";
        var localNew = Path.Combine(localOverride, "local-new.txt");
        await File.WriteAllTextAsync(localNew, newContent);
        var push = await cloud.SyncChangesAsync(account.Id, pair);
        Console.WriteLine($"  pushed new local file: {push.Completed} op(s), {push.Failures.Count} failed");

        var r4 = cloud.FreeUpSpace(pair);
        var newOffloaded = (Attrs(localNew) & RecallOnDataAccess) != 0;
        Console.WriteLine($"  finalize + free up: dehydrated={r4.Dehydrated} (expect 2: hello + local-new), local-new cloud-only={newOffloaded}  -> {(newOffloaded ? "PASS" : "FAIL")}");
        ok = ok && newOffloaded;

        var read3 = await File.ReadAllTextAsync(localNew);
        Console.WriteLine($"  re-hydrate converted file: content match={read3 == newContent}  -> {(read3 == newContent ? "PASS" : "FAIL")}");
        ok = ok && read3 == newContent;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Test error: {ex.GetType().Name}: {ex.Message}");
        ok = false;
    }
    finally
    {
        cloud.Disable(pair.Id);
        try { engine.UnregisterSyncRoot(localOverride); } catch { }
        new JsonSyncStateStore(paths).Clear(pair.Id);
        new JsonPopulatedFolderStore(paths).Clear(pair.Id);

        await using var cleanup = await ConnectFromStoredAsync().ConfigureAwait(false);
        var node = cleanup is null ? null : await cleanup.ResolvePathAsync(testRoot).ConfigureAwait(false);
        if (node is not null)
        {
            await cleanup!.TrashAsync(node).ConfigureAwait(false);
            Console.WriteLine("\n  cleaned up the remote test folder.");
        }
    }

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// Reproduces the user's "pause mid-upload" crash scenario headlessly: enable an on-demand pair with a
// throttled uploader, drop a file big enough that the auto-push takes ~15s, then StopAutoSync (the exact
// pause path: FolderWatcher.Dispose → CTS.Cancel mid-HTTP) while the upload is in flight. Unhandled and
// unobserved exceptions are reported; the test passes if the process survives and a resumed auto-sync
// then completes the upload. `--pausetest <localFolder> <remotePath>`.
static async Task<int> PauseTestAsync(string? localOverride, string? remoteOverride)
{
    if (localOverride is null || remoteOverride is null)
    {
        Console.WriteLine("  x Usage: --pausetest <localFolder> <remotePath>");
        return 1;
    }

    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        Console.WriteLine($"\n  [UNHANDLED — this is what crashes the app]\n{e.ExceptionObject}");
    TaskScheduler.UnobservedTaskException += (_, e) =>
        Console.WriteLine($"\n  [UNOBSERVED TASK EXCEPTION]\n{e.Exception}");

    var paths = new PawsPaths();
    var account = new JsonSettingsStore(paths).Load().Accounts.FirstOrDefault();
    if (account is null)
    {
        Console.WriteLine("  x No account configured.");
        return 1;
    }

    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    var testRoot = $"{remoteOverride.TrimEnd('/')}/_pausetest_{stamp}";

    await using (var setup = await ConnectFromStoredAsync().ConfigureAwait(false))
    {
        if (setup is null)
        {
            return 1;
        }

        var parent = await setup.ResolvePathAsync(remoteOverride).ConfigureAwait(false);
        if (parent is null)
        {
            Console.WriteLine($"  x Remote folder not found: {remoteOverride}");
            return 1;
        }

        await setup.CreateFolderAsync(parent, $"_pausetest_{stamp}").ConfigureAwait(false);
    }

    Console.WriteLine($"PAWS - pause-mid-upload test\n  local  : {localOverride}\n  remote : {testRoot}\n");

    Directory.CreateDirectory(localOverride);
    var engine = new CloudFilterPlaceholderEngine();
    // Throttle the upload to 512 KB/s so an 8 MB file reliably takes ~16s — plenty of window to pause
    // mid-transfer. This also matches the app, where ThrottledStream is always in the stream chain.
    var throttle = new TransferThrottle { UploadLimitKBps = 512 };
    await using var cloud = new CloudSyncService(
        engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths),
        new JsonPopulatedFolderStore(paths), new SemaphoreSlim(1, 1), throttle);
    var pair = new SyncPair { Id = $"pausetest{stamp}", LocalPath = localOverride, RemotePath = testRoot, Mode = SyncMode.OnDemand };

    var syncStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pushed = new TaskCompletionSource<SyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    cloud.AutoSyncStarted += _ =>
    {
        Console.WriteLine($"  [auto] sync started ({DateTime.Now:HH:mm:ss})");
        syncStarted.TrySetResult();
    };
    cloud.AutoSyncCompleted += e =>
    {
        Console.WriteLine(e.Succeeded
            ? $"  [auto] completed — pushed {e.Result!.Completed}, failed {e.Result.Failures.Count} ({DateTime.Now:HH:mm:ss})"
            : $"  [auto] error: {e.Error!.GetType().Name}: {e.Error.Message} ({DateTime.Now:HH:mm:ss})");
        if (e.Succeeded && e.Result!.Completed > 0 && e.Result.Failures.Count == 0)
        {
            pushed.TrySetResult(e.Result);
        }
    };

    var ok = true;
    try
    {
        await cloud.EnableAsync(account.Id, pair);
        cloud.StartAutoSync(account.Id, pair);

        // 8 MB of random bytes → ~16s upload at 512 KB/s.
        var payload = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        var fileName = $"bigfile-{stamp}.bin";
        await File.WriteAllBytesAsync(Path.Combine(localOverride, fileName), payload);
        Console.WriteLine($"  wrote {fileName} (8 MB) — waiting for auto-sync to start…");

        if (await Task.WhenAny(syncStarted.Task, Task.Delay(TimeSpan.FromSeconds(30))) != syncStarted.Task)
        {
            Console.WriteLine("  x auto-sync never started.");
            return 1;
        }

        // Let the upload get well underway, then pause exactly like the UI's ⏸ button does.
        await Task.Delay(TimeSpan.FromSeconds(6));
        Console.WriteLine($"  PAUSING mid-upload (StopAutoSync) ({DateTime.Now:HH:mm:ss})…");
        cloud.StopAutoSync(pair.Id);
        Console.WriteLine("  StopAutoSync returned — waiting 10s for any delayed fallout…");

        await Task.Delay(TimeSpan.FromSeconds(10));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Console.WriteLine("  process is still alive after the pause. Resuming to verify recovery…");

        // Resume — the watcher should retry the (aborted) upload and finish it.
        cloud.StartAutoSync(account.Id, pair);
        File.SetLastWriteTimeUtc(Path.Combine(localOverride, fileName), DateTime.UtcNow); // nudge the watcher
        var finished = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        var resumed = finished == pushed.Task;
        Console.WriteLine($"  resumed upload completed: {(resumed ? "YES" : "NO (timed out)")}");
        ok = resumed;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  x Test error: {ex.GetType().Name}: {ex.Message}");
        ok = false;
    }
    finally
    {
        cloud.StopAutoSync(pair.Id);
        cloud.Disable(pair.Id);
        try { engine.UnregisterSyncRoot(localOverride); } catch { }
        new JsonSyncStateStore(paths).Clear(pair.Id);
        new JsonPopulatedFolderStore(paths).Clear(pair.Id);

        await using var cleanup = await ConnectFromStoredAsync().ConfigureAwait(false);
        var node = cleanup is null ? null : await cleanup.ResolvePathAsync(testRoot).ConfigureAwait(false);
        if (node is not null)
        {
            await cleanup!.TrashAsync(node).ConfigureAwait(false);
            Console.WriteLine("  cleaned up the remote test folder.");
        }
    }

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// OFFLINE timing test for the transfer speed limiter: pushes a known payload through ThrottledStream
// at a fixed rate and asserts elapsed time matches the budget (write/download path, read/upload path,
// and unlimited passthrough). `--throttletest`.
static async Task<int> ThrottleTestAsync()
{
    const int limitKBps = 256;
    var payload = new byte[768 * 1024]; // 3 seconds of budget at 256 KB/s

    // Download path: throttled writes.
    var downloadThrottle = new TransferThrottle { DownloadLimitKBps = limitKBps };
    var sw = Stopwatch.StartNew();
    await using (var dest = downloadThrottle.WrapDownloadDestination(new MemoryStream()))
    {
        await dest.WriteAsync(payload);
    }

    sw.Stop();
    var writeOk = sw.Elapsed.TotalSeconds is >= 2.0 and <= 5.0;
    Console.WriteLine($"  throttled WRITE  768 KB @ {limitKBps} KB/s: {sw.Elapsed.TotalSeconds:0.00}s (expect ~3s)  -> {(writeOk ? "PASS" : "FAIL")}");

    // Upload path: throttled reads.
    var uploadThrottle = new TransferThrottle { UploadLimitKBps = limitKBps };
    sw.Restart();
    await using (var source = uploadThrottle.WrapUploadSource(new MemoryStream(payload)))
    {
        var buffer = new byte[81920];
        while (await source.ReadAsync(buffer) > 0)
        {
        }
    }

    sw.Stop();
    var readOk = sw.Elapsed.TotalSeconds is >= 2.0 and <= 5.0;
    Console.WriteLine($"  throttled READ   768 KB @ {limitKBps} KB/s: {sw.Elapsed.TotalSeconds:0.00}s (expect ~3s)  -> {(readOk ? "PASS" : "FAIL")}");

    // No limit set: passthrough must be effectively instant.
    var unlimited = new TransferThrottle();
    sw.Restart();
    await using (var dest = unlimited.WrapDownloadDestination(new MemoryStream()))
    {
        await dest.WriteAsync(payload);
    }

    sw.Stop();
    var passOk = sw.Elapsed.TotalSeconds < 0.5;
    Console.WriteLine($"  unlimited WRITE  768 KB:                    {sw.Elapsed.TotalSeconds:0.00}s (expect ~0s)  -> {(passOk ? "PASS" : "FAIL")}");

    var ok = writeOk && readOk && passOk;
    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// Prints the drive.proton.me web URL for a remote path (verifies the volumes API call + URL format).
// `--weburl <remotePath>`.
static async Task<int> WebUrlAsync(string? path)
{
    await using var drive = await ConnectFromStoredAsync().ConfigureAwait(false);
    if (drive is null)
    {
        return 1;
    }

    var remote = string.IsNullOrWhiteSpace(path) ? "/" : path;
    var node = await drive.ResolvePathAsync(remote).ConfigureAwait(false);
    if (node is null)
    {
        Console.WriteLine($"  x Not found on Drive: {remote}");
        return 1;
    }

    var url = await drive.GetWebUrlAsync(node).ConfigureAwait(false);
    Console.WriteLine(url is null ? "  x Could not determine the web URL." : $"  {remote}  ->  {url}");
    return url is null ? 1 : 0;
}

// OFFLINE cfapi-only regression (no Proton) for the lazy-population + metadata-corruption fix. Uses
// realistic long Proton-style composite identities (the ones that used to corrupt) and verifies, from a
// SEPARATE process (a real browse, since same-process enumeration never fires FETCH_PLACEHOLDERS):
//   1. the eagerly-created childless sub/ placeholder enumerates without STATUS_CLOUD_FILE_METADATA_CORRUPT
//      (guards the batched-CfCreatePlaceholders identity-overrun bug — must be single-element);
//   2. browsing sub/ populates it lazily via FETCH_PLACEHOLDERS (long-identity file + nested folder);
//   3. browsing the transfer-created nested folder recurses and populates too.
// `--dirtest <localFolder>`.
static int DirTest(string? localOverride, string? _)
{
    if (localOverride is null)
    {
        Console.WriteLine("  x Usage: --dirtest <localFolder>");
        return 1;
    }

    var engine = new CloudFilterPlaceholderEngine();
    Directory.CreateDirectory(localOverride);
    engine.RegisterSyncRoot(new SyncRootInfo(localOverride, "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64", "PAWS", "1.0.0.0"));

    var now = DateTimeOffset.UtcNow;
    string LongId(string tag) => "iAxR7cgYefZJzyNS1S1MbQ~pjMdBpjbtK7iGaTg-UgH-g~XsODA2mF5rj49K_" + tag; // ~64 chars
    RemoteEntry MkFile(string path, string tag) => new() { RelativePath = path, Name = path.Split('/')[^1], IsFolder = false, Size = 3, ModifiedUtc = now, Uid = LongId(tag), RevisionUid = LongId(tag + "r") };
    RemoteEntry MkDir(string path, string tag) => new() { RelativePath = path, Name = path.Split('/')[^1], IsFolder = true, Size = null, ModifiedUtc = now, Uid = "iAxR7cgYefZJzyNS1S1MbQ~zgzJvzyxOEdz_" + tag };

    // Shallow eager root: a long-identity FILE next to a childless FOLDER (the exact shape that corrupted).
    engine.CreatePlaceholders(localOverride, new RemoteSnapshot
    {
        RootPath = "/",
        CapturedUtc = now,
        Entries = new List<RemoteEntry> { MkFile("root.txt", "root"), MkDir("sub", "sub") },
    });

    using var conn = engine.Connect(localOverride,
        (rel, ct) => Task.FromResult<IReadOnlyList<RemoteEntry>>(rel switch
        {
            "sub" => new List<RemoteEntry> { MkFile("sub/bigfile.txt", "bf"), MkDir("sub/deep", "deep") },
            "sub/deep" => new List<RemoteEntry> { MkFile("sub/deep/leaf.txt", "lf") },
            _ => new List<RemoteEntry>(),
        }),
        (id, output, ct) => Task.CompletedTask);

    var ok = true;
    var (subOk, sub) = EnumerateInSeparateProcess(Path.Combine(localOverride, "sub"));
    Console.WriteLine($"  browse sub/       -> {(subOk ? $"[{string.Join(",", sub)}]" : sub[0])}");
    ok = ok && subOk && sub.Contains("bigfile.txt") && sub.Contains("deep");

    var (deepOk, deep) = EnumerateInSeparateProcess(Path.Combine(localOverride, "sub", "deep"));
    Console.WriteLine($"  browse sub/deep/  -> {(deepOk ? $"[{string.Join(",", deep)}]" : deep[0])}");
    ok = ok && deepOk && deep.Contains("leaf.txt");

    conn.Dispose();
    try { engine.UnregisterSyncRoot(localOverride); } catch { }
    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// OFFLINE pure self-test (no Proton, no cfapi) for conflict-resolution planning: every conflict shape
// the reconciler can emit × every ConflictResolution must map to exactly the right concrete steps —
// including the deletion-flavored conflicts and the unresolvable file-vs-folder mismatch — plus the
// conflict-copy rename (name shape + uniquification). `--conflicttest`.
static int ConflictPlanSelfTest()
{
    var ok = true;
    void Case(string name, bool pass)
    {
        Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name}");
        ok &= pass;
    }

    var remoteFile = new RemoteEntry { RelativePath = "doc.txt", Name = "doc.txt", IsFolder = false, Size = 10, Uid = "vol~u", RevisionUid = "vol~u~r1" };
    var localFile = new LocalEntry { RelativePath = "doc.txt", Name = "doc.txt", IsFolder = false, Size = 12, ModifiedUtc = DateTimeOffset.UtcNow };
    var remoteFolder = new RemoteEntry { RelativePath = "doc.txt", Name = "doc.txt", IsFolder = true, Uid = "vol~d" };

    SyncOperation Conflict(RemoteEntry? r, LocalEntry? l) => new()
    {
        Kind = SyncOperationKind.Conflict, RelativePath = "doc.txt", IsFolder = false, Remote = r, Local = l,
    };

    var bothChanged = Conflict(remoteFile, localFile);          // changed on both sides / no-history different size
    var deletedLocally = Conflict(remoteFile, null);            // deleted locally, remote changed
    var deletedRemotely = Conflict(null, localFile);            // deleted remotely, local changed
    var typeMismatch = Conflict(remoteFolder, localFile);       // file vs folder

    ConflictPlan? P(SyncOperation c, ConflictResolution r) => SyncExecutor.PlanResolution(c, r);
    bool Steps(ConflictPlan? p, bool rename, params SyncOperationKind[] kinds)
        => p is not null && p.RenameLocalToConflictCopy == rename
           && p.Operations.Select(o => o.Kind).SequenceEqual(kinds);

    Case("both-changed + KeepLocal  -> upload", Steps(P(bothChanged, ConflictResolution.KeepLocal), false, SyncOperationKind.UploadFile));
    Case("both-changed + KeepRemote -> download", Steps(P(bothChanged, ConflictResolution.KeepRemote), false, SyncOperationKind.DownloadFile));
    Case("both-changed + KeepBoth   -> rename + download", Steps(P(bothChanged, ConflictResolution.KeepBoth), true, SyncOperationKind.DownloadFile));
    Case("both-changed + KeepBoth   downloads fresh (no stale Local)", P(bothChanged, ConflictResolution.KeepBoth)!.Operations[0].Local is null);

    Case("deleted-locally + KeepLocal  -> trash remote", Steps(P(deletedLocally, ConflictResolution.KeepLocal), false, SyncOperationKind.DeleteRemote));
    Case("deleted-locally + KeepRemote -> download", Steps(P(deletedLocally, ConflictResolution.KeepRemote), false, SyncOperationKind.DownloadFile));
    Case("deleted-locally + KeepBoth   -> download only (nothing local to rename)", Steps(P(deletedLocally, ConflictResolution.KeepBoth), false, SyncOperationKind.DownloadFile));

    Case("deleted-remotely + KeepLocal  -> upload", Steps(P(deletedRemotely, ConflictResolution.KeepLocal), false, SyncOperationKind.UploadFile));
    Case("deleted-remotely + KeepRemote -> delete local", Steps(P(deletedRemotely, ConflictResolution.KeepRemote), false, SyncOperationKind.DeleteLocal));
    Case("deleted-remotely + KeepBoth   -> rename only", Steps(P(deletedRemotely, ConflictResolution.KeepBoth), true));

    Case("type mismatch is unresolvable (all three)",
        P(typeMismatch, ConflictResolution.KeepLocal) is null
        && P(typeMismatch, ConflictResolution.KeepRemote) is null
        && P(typeMismatch, ConflictResolution.KeepBoth) is null);
    Case("non-conflict op is not planned",
        SyncExecutor.PlanResolution(bothChanged with { Kind = SyncOperationKind.UploadFile }, ConflictResolution.KeepLocal) is null);

    // Conflict-copy rename: name shape + uniquification when resolved twice in the same minute.
    var root = Path.Combine(Path.GetTempPath(), "paws-conflicttest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "one");
        var first = SyncExecutor.RenameToConflictCopy(root, "a.txt");
        File.WriteAllText(Path.Combine(root, "a.txt"), "two");
        var second = SyncExecutor.RenameToConflictCopy(root, "a.txt");

        Case("rename moves the file aside", !File.Exists(Path.Combine(root, "a.txt")) && File.Exists(first));
        Case("copy is named \"a (conflict copy …).txt\"",
            Path.GetFileName(first).StartsWith("a (conflict copy ", StringComparison.Ordinal) && first.EndsWith(".txt", StringComparison.Ordinal));
        Case("second copy is uniquified, both kept",
            File.Exists(first) && File.Exists(second) && !string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
            && File.ReadAllText(first) == "one" && File.ReadAllText(second) == "two");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// OFFLINE cfapi-only repro/regression (no Proton) for the TRANSFER_PLACEHOLDERS edge paths that a normal
// browse never hits: (1) enumerating an EMPTY remote folder (zero placeholders, success status) and
// (2) a folder whose live listing THROWS (e.g. Drive session resume fails after a force-close
// mid-upload) — the provider then reports the enumeration failed with an empty placeholder array.
// A crash here reproduces the startup access violation at SendPlaceholders/CfExecute.
// `--popfailtest <localFolder>`.
static int PopFailTest(string? localOverride)
{
    if (localOverride is null)
    {
        Console.WriteLine("  x Usage: --popfailtest <localFolder>");
        return 1;
    }

    var engine = new CloudFilterPlaceholderEngine();
    Directory.CreateDirectory(localOverride);
    engine.RegisterSyncRoot(new SyncRootInfo(localOverride, "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64", "PAWS", "1.0.0.0"));

    var now = DateTimeOffset.UtcNow;
    engine.CreatePlaceholders(localOverride, new RemoteSnapshot
    {
        RootPath = "/",
        CapturedUtc = now,
        Entries = new List<RemoteEntry>
        {
            new() { RelativePath = "emptydir", Name = "emptydir", IsFolder = true, ModifiedUtc = now, Uid = "vol~empty" },
            new() { RelativePath = "faildir", Name = "faildir", IsFolder = true, ModifiedUtc = now, Uid = "vol~fail" },
        },
    });

    using var conn = engine.Connect(
        localOverride,
        (rel, _) => rel switch
        {
            "emptydir" => Task.FromResult<IReadOnlyList<RemoteEntry>>(new List<RemoteEntry>()),
            "faildir" => Task.FromException<IReadOnlyList<RemoteEntry>>(new InvalidOperationException("simulated listing failure")),
            _ => Task.FromResult<IReadOnlyList<RemoteEntry>>(new List<RemoteEntry>()),
        },
        (_, _, _) => Task.CompletedTask);

    var ok = true;

    Console.WriteLine("  browsing emptydir (zero children, success)...");
    var (emptyOk, emptyNames) = EnumerateInSeparateProcess(Path.Combine(localOverride, "emptydir"));
    Console.WriteLine($"    -> ok={emptyOk} [{string.Join(",", emptyNames)}]");
    ok &= emptyOk && emptyNames.Count == 0;

    Console.WriteLine("  browsing faildir (listing throws -> enumeration reported failed)...");
    var (failOk, failNames) = EnumerateInSeparateProcess(Path.Combine(localOverride, "faildir"));
    Console.WriteLine($"    -> ok={failOk} [{string.Join(",", failNames)}] (a FAILED dir here is acceptable; a process crash is the bug)");

    // Browse faildir again — population must still be enabled (the failure path must not have latched
    // DISABLE_ON_DEMAND_POPULATION), and the provider must still be alive to answer.
    Console.WriteLine("  browsing faildir again (retry must still reach the provider)...");
    var (retryOk, retryNames) = EnumerateInSeparateProcess(Path.Combine(localOverride, "faildir"));
    Console.WriteLine($"    -> ok={retryOk} [{string.Join(",", retryNames)}]");

    conn.Dispose();
    try
    {
        engine.UnregisterSyncRoot(localOverride);
    }
    catch
    {
    }

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")} (and we did not crash)");
    return ok ? 0 : 1;
}

// OFFLINE cfapi-only regression (no Proton) for stopping sync / removing an account / resetting the app:
// DecommissionTree must return the folder to a NORMAL folder BEFORE the sync root is unregistered.
// Keep-files mode: a hydrated placeholder reverts to a plain file (content kept), a cloud-only
// placeholder is deleted (its content lives on the remote), a plain local file is untouched, and a
// placeholder folder with nothing local left inside is removed. Remove-files mode: everything under the
// root is deleted. `--removetest <localFolder>`.
static int RemoveTest(string? localOverride)
{
    if (localOverride is null)
    {
        Console.WriteLine("  x Usage: --removetest <localFolder>");
        return 1;
    }

    var engine = new CloudFilterPlaceholderEngine();
    Directory.CreateDirectory(localOverride);
    var rootInfo = new SyncRootInfo(localOverride, "30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64", "PAWS", "1.0.0.0");
    engine.RegisterSyncRoot(rootInfo);

    var now = DateTimeOffset.UtcNow;
    var content = Encoding.UTF8.GetBytes("hello from the cloud! this file was hydrated on demand.");
    RemoteEntry MkFile(string path, string id, long size) => new()
    {
        RelativePath = path, Name = path.Split('/')[^1], IsFolder = false, Size = size, ModifiedUtc = now,
        Uid = "vol~" + id, RevisionUid = "vol~" + id + "~r1",
    };

    engine.CreatePlaceholders(localOverride, new RemoteSnapshot
    {
        RootPath = "/",
        CapturedUtc = now,
        Entries = new List<RemoteEntry>
        {
            MkFile("hydrated.txt", "hyd", content.Length),
            MkFile("cloudonly.txt", "cld", 5),
            new() { RelativePath = "sub", Name = "sub", IsFolder = true, ModifiedUtc = now, Uid = "vol~sub" },
            MkFile("sub/inner.txt", "inn", 5),
            new() { RelativePath = "sub2", Name = "sub2", IsFolder = true, ModifiedUtc = now, Uid = "vol~sub2" },
        },
    });

    // Local-file creation and hydration both need the provider connected (an unpopulated on-demand
    // directory refuses new files until the provider answers for it) — matching the real app, where the
    // provider runs for the folder's whole lifetime. Disconnect before decommissioning, like the service.
    using (engine.Connect(
        localOverride,
        (rel, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>(rel switch
        {
            "sub2" => new List<RemoteEntry> { MkFile("sub2/keep2.txt", "kp2", content.Length) },
            _ => new List<RemoteEntry>(),
        }),
        async (_, output, ct) => await output.WriteAsync(content, ct)))
    {
        File.WriteAllText(Path.Combine(localOverride, "local.txt"), "born local, never a placeholder");
        var bytes = File.ReadAllBytes(Path.Combine(localOverride, "hydrated.txt"));
        Console.WriteLine($"  hydrate hydrated.txt -> {bytes.Length} B (content match: {bytes.AsSpan().SequenceEqual(content)})");

        // Browse sub2 from a SEPARATE process so it gets POPULATED (the real Explorer flow), then
        // hydrate its file — a populated folder with local content must survive as a plain folder.
        var (browseOk, names) = EnumerateInSeparateProcess(Path.Combine(localOverride, "sub2"));
        var bytes2 = File.ReadAllBytes(Path.Combine(localOverride, "sub2", "keep2.txt"));
        Console.WriteLine($"  browse+hydrate sub2/keep2.txt -> browse={browseOk} [{string.Join(",", names)}], {bytes2.Length} B (match: {bytes2.AsSpan().SequenceEqual(content)})");
    }

    // Diagnostic: what does each entry look like right before decommission? (Attributes vs the
    // authoritative Cloud Filter placeholder state — under shell registration these can disagree.)
    foreach (var entry in Directory.EnumerateFileSystemEntries(localOverride, "*", SearchOption.AllDirectories))
    {
        var attributes = File.GetAttributes(entry);
        var handle = Vanara.PInvoke.Kernel32.FindFirstFile(entry, out var findData);
        var state = handle.IsInvalid
            ? Vanara.PInvoke.CldApi.CF_PLACEHOLDER_STATE.CF_PLACEHOLDER_STATE_INVALID
            : Vanara.PInvoke.CldApi.CfGetPlaceholderStateFromFindData(findData);
        handle.Dispose();
        Console.WriteLine($"    {Path.GetRelativePath(localOverride, entry),-16} attrs=0x{(int)attributes:X8} state={state}");
    }

    var keep = engine.DecommissionTree(localOverride, keepLocalFiles: true);
    Console.WriteLine($"  decommission(keep files) -> reverted={keep.Reverted} deleted={keep.Deleted} kept={keep.Kept} errors={keep.Errors.Count}");
    foreach (var error in keep.Errors)
    {
        Console.WriteLine($"    ! {error}");
    }

    try
    {
        engine.UnregisterSyncRoot(localOverride);
        Console.WriteLine("  unregister sync root -> OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  unregister sync root -> FAILED: {ex.Message}");
    }

    var ok = true;
    bool Report(string what, bool pass)
    {
        Console.WriteLine($"  {(pass ? "OK " : "x  ")} {what}");
        return pass;
    }

    bool IsPlainFile(string name)
    {
        var path = Path.Combine(localOverride, name);
        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }

    ok &= Report("hydrated.txt is now a plain file with its content",
        IsPlainFile("hydrated.txt") && File.ReadAllBytes(Path.Combine(localOverride, "hydrated.txt")).AsSpan().SequenceEqual(content));
    ok &= Report("local.txt untouched", IsPlainFile("local.txt"));
    ok &= Report("cloudonly.txt removed (content lives on the remote)", !File.Exists(Path.Combine(localOverride, "cloudonly.txt")));
    ok &= Report("sub/ removed (nothing local was inside it)", !Directory.Exists(Path.Combine(localOverride, "sub")));
    ok &= Report("sub2/ kept as a plain folder (had local content)", Directory.Exists(Path.Combine(localOverride, "sub2")));
    ok &= Report("sub2/keep2.txt is a plain file with its content",
        IsPlainFile(Path.Combine("sub2", "keep2.txt"))
        && File.ReadAllBytes(Path.Combine(localOverride, "sub2", "keep2.txt")).AsSpan().SequenceEqual(content));

    // Round 2: remove-files mode — the folder is emptied (still never touching the remote). The plain
    // file goes in before re-registering (no provider is connected to answer population requests).
    File.WriteAllText(Path.Combine(localOverride, "another-local.txt"), "x");
    engine.RegisterSyncRoot(rootInfo);
    engine.CreatePlaceholders(localOverride, new RemoteSnapshot
    {
        RootPath = "/",
        CapturedUtc = now,
        Entries = new List<RemoteEntry> { MkFile("cloudonly2.txt", "cl2", 5) },
    });

    var wipe = engine.DecommissionTree(localOverride, keepLocalFiles: false);
    Console.WriteLine($"  decommission(remove files) -> deleted={wipe.Deleted} errors={wipe.Errors.Count}");
    try
    {
        engine.UnregisterSyncRoot(localOverride);
    }
    catch
    {
    }

    ok &= Report("remove-files leaves the folder empty", !Directory.EnumerateFileSystemEntries(localOverride).Any());

    Console.WriteLine($"\n  RESULT: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
}

// Enumerates a folder from a SEPARATE process (a fresh cmd.exe) — a real browse that triggers Cloud
// Filter's FETCH_PLACEHOLDERS, unlike an in-process enumeration by the provider itself. Returns the child
// names, or (false, [errorText]) if the enumeration failed (e.g. the metadata-corrupt IOException).
static (bool ok, List<string> names) EnumerateInSeparateProcess(string folder)
{
    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c dir /b \"{folder}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var proc = System.Diagnostics.Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();

    if (proc.ExitCode != 0)
    {
        return (false, new List<string> { $"dir failed: {stderr.Trim()}" });
    }

    var names = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    return (true, names);
}

// Lazy per-folder children listing for the harness (mirrors CloudSyncService.CreateFetchChildren): lists
// one remote folder's children and maps them to sync-root-relative RemoteEntry.
static FetchFolderChildren MakeTestFetchChildren(IProtonDriveClient drive, string remoteRoot) => async (relativeFolder, ct) =>
{
    var remotePath = string.IsNullOrEmpty(relativeFolder) ? remoteRoot : $"{remoteRoot.TrimEnd('/')}/{relativeFolder}";
    var entries = new List<RemoteEntry>();
    var folder = await drive.ResolvePathAsync(remotePath, ct).ConfigureAwait(false);
    if (folder is null)
    {
        return entries;
    }

    await foreach (var child in drive.ListChildrenAsync(folder, ct).ConfigureAwait(false))
    {
        var rel = string.IsNullOrEmpty(relativeFolder) ? child.Name : $"{relativeFolder}/{child.Name}";
        entries.Add(new RemoteEntry
        {
            RelativePath = rel,
            Name = child.Name,
            IsFolder = child.IsFolder,
            Size = child.Size,
            ModifiedUtc = child.ModifiedUtc,
            Uid = child.Uid,
            ParentUid = child.ParentUid,
            RevisionUid = child.RevisionUid,
        });
    }

    return entries;
};

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
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new JsonPopulatedFolderStore(paths), new SemaphoreSlim(1, 1));
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
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new JsonPopulatedFolderStore(paths), new SemaphoreSlim(1, 1));
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
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new JsonPopulatedFolderStore(paths), new SemaphoreSlim(1, 1));
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
    await using var cloud = new CloudSyncService(engine, new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths), new JsonPopulatedFolderStore(paths), new SemaphoreSlim(1, 1));
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
