using System.Diagnostics;
using System.Text;
using PAWS.Core.Drive;
using PAWS.Infrastructure.Proton;
using PAWS.Proton;
using PAWS.Proton.Drive;

Console.OutputEncoding = Encoding.UTF8;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "drive";
var doWrite = args.Contains("--write", StringComparer.OrdinalIgnoreCase);

return mode switch
{
    "--cryptocheck" or "cryptocheck" => CryptoCheck(),
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

// End-to-end: browser login -> resume SDK session -> Drive list (and optional upload/download/trash).
static async Task<int> DriveAsync(bool doWrite)
{
    Console.WriteLine("PAWS - Proton Drive test\n");

    // 1) Browser/session-fork login (no password handled here).
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
        return 1;
    }

    var session = auth.Session!;
    Console.WriteLine($"  + Signed in as {session.Username}\n");

    // 2) Resume the SDK session and open the Drive client.
    await using var drive = new ProtonDriveClientAdapter(await ProtonSessionConnector.ResumeAsync(session).ConfigureAwait(false));
    await drive.ConnectAsync().ConfigureAwait(false);

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

    await drive.TrashAsync(uploaded).ConfigureAwait(false);
    Console.WriteLine("  trashed    : test file moved to Drive trash");

    return ok ? 0 : 1;
}
