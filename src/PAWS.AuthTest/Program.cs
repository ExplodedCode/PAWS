using System.Diagnostics;
using System.Text;
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
    _ => await DriveAsync(doWrite),
};

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

    var engine = new SyncEngine(new ProtonDriveClientFactory(new DpapiSecretStore(paths)), new JsonSyncStateStore(paths));

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
