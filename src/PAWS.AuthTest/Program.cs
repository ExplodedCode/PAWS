using System.Diagnostics;
using System.Text;
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

return mode switch
{
    "--cryptocheck" or "cryptocheck" => CryptoCheck(),
    "--snapshot" or "snapshot" => await SnapshotAsync(pathArg),
    "--trash" or "trash" => await TrashPathAsync(pathArg),
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
