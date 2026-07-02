using System.Diagnostics;
using System.Text;
using PAWS.Core.Abstractions;
using PAWS.Core.Configuration;
using PAWS.Core.Proton;
using PAWS.Core.Setup;
using PAWS.Infrastructure.Proton;
using PAWS.Infrastructure.Storage;

namespace PAWS.Setup;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "weblogin";

        return mode switch
        {
            "--selftest" or "selftest" => SelfTest(),
            "--show" or "show" => Show(BuildPaths()),
            "--reset" or "reset" => Reset(BuildPaths()),
            "--help" or "-h" or "help" => Help(),
            _ => await WebLoginAsync().ConfigureAwait(false),
        };
    }

    private static PawsPaths BuildPaths() => new();

    // ---------------------------------------------------------------- web (browser) login ----

    /// <summary>
    /// The only login path: sign in on Proton's website (session forking). No password is ever typed
    /// into PAWS — passkeys, 2FA and CAPTCHA are all handled by Proton.
    /// </summary>
    private static async Task<int> WebLoginAsync()
    {
        var paths = BuildPaths();
        var settingsStore = new JsonSettingsStore(paths);
        var secretStore = new DpapiSecretStore(paths);
        var workflow = new SetupWorkflow(settingsStore, secretStore);
        var web = new WebProtonAuthenticator();

        Header();
        Console.WriteLine($"Config folder: {paths.Root}");
        Console.WriteLine("\nSign in with your browser (supports passkeys / 2FA). A Proton login page will open.\n");

        var result = await web.SignInAsync(challenge =>
        {
            Console.WriteLine($"Opening: {challenge.Url}\n");
            try
            {
                Process.Start(new ProcessStartInfo { FileName = challenge.Url, UseShellExecute = true });
            }
            catch
            {
                Console.WriteLine("(Could not open a browser automatically - paste the URL above into your browser.)");
            }

            Console.WriteLine("Waiting for you to finish signing in on the website…");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            Console.WriteLine($"\n  x {result.Status}: {result.Message}");
            return 1;
        }

        var session = result.Session!;
        var added = workflow.AddAccount(session);

        Console.WriteLine($"\n  + Signed in as {session.Username}");
        Console.WriteLine($"  + Account {added.Account!.Id[..8]} stored securely (DPAPI).");
        Console.WriteLine($"    session id   : {session.SessionId}");
        Console.WriteLine($"    key password : {(string.IsNullOrEmpty(session.DataPassword) ? "(none)" : "recovered + stored")}");
        Console.WriteLine("\nRun with --show to see configured accounts.");
        return 0;
    }

    // ---------------------------------------------------------------- other commands ----

    private static int Show(PawsPaths paths)
    {
        Header();
        Console.WriteLine($"Config folder : {paths.Root}\n");
        ShowAccounts(new JsonSettingsStore(paths), new DpapiSecretStore(paths));
        return 0;
    }

    private static void ShowAccounts(ISettingsStore settingsStore, ISecretStore secretStore)
    {
        var settings = settingsStore.Load();
        if (settings.Accounts.Count == 0)
        {
            Console.WriteLine("No accounts configured yet.");
            return;
        }

        Console.WriteLine($"{settings.Accounts.Count} account(s):");
        foreach (var account in settings.Accounts)
        {
            var hasSecrets = secretStore.HasSecrets(account.Id);
            Console.WriteLine($"\n  • {account.Label}   [id {account.Id[..8]}]   secrets={(hasSecrets ? "stored" : "MISSING")}");
            if (account.SyncPairs.Count == 0)
            {
                Console.WriteLine("      (no folders)");
            }

            foreach (var p in account.SyncPairs)
            {
                Console.WriteLine($"      \"{p.LocalPath}\"  <=>  \"{p.RemotePath}\"  [{p.Mode}]");
            }
        }
    }

    private static int Reset(PawsPaths paths)
    {
        var settingsStore = new JsonSettingsStore(paths);
        var secretStore = new DpapiSecretStore(paths);

        foreach (var account in settingsStore.Load().Accounts)
        {
            secretStore.ClearSecrets(account.Id);
        }

        if (File.Exists(paths.SettingsFile))
        {
            File.Delete(paths.SettingsFile);
        }

        Console.WriteLine("Cleared all stored credentials and settings.");
        return 0;
    }

    private static int Help()
    {
        Header();
        Console.WriteLine("Usage: PAWS.Setup [command]\n");
        Console.WriteLine("  (no args)    Sign in with your browser (passkeys/2FA) and add an account + folder.");
        Console.WriteLine("  --show       List configured accounts and their folders (secrets redacted).");
        Console.WriteLine("  --reset      Delete ALL stored credentials and settings.");
        Console.WriteLine("  --selftest   Non-interactive multi-account storage round-trip check (temp folder).");
        return 0;
    }

    // ---------------------------------------------------------------- self test ----

    /// <summary>
    /// Exercises the multi-account persistence pipeline without user input: add two accounts (incl.
    /// extra folder), reload, assert, then remove one. Uses synthetic browser-style sessions and a
    /// throwaway temp folder. Returns 0 on success, non-zero on failure (suitable for CI).
    /// </summary>
    private static int SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "PAWS_selftest_" + Guid.NewGuid().ToString("n"));
        var paths = new PawsPaths(root);
        Header();
        Console.WriteLine($"Self-test root: {root}\n");

        try
        {
            var settingsStore = new JsonSettingsStore(paths);
            var secretStore = new DpapiSecretStore(paths);
            var workflow = new SetupWorkflow(settingsStore, secretStore);

            var personal = workflow.AddAccount(
                FakeSession("personal@proton.me", "correct horse battery staple"),
                new SyncPair { LocalPath = @"C:\Users\me\Personal", RemotePath = "/Personal", Mode = SyncMode.OnDemand },
                "Personal");

            var work = workflow.AddAccount(
                FakeSession("work@proton.me", "tr0ub4dor&3"),
                new SyncPair { LocalPath = @"C:\Users\me\Work", RemotePath = "/Work", Mode = SyncMode.FullSync },
                "Work");

            // Same account can be added again, and accounts can have multiple folders.
            // (Was Mode = SyncMode.CloudOnly — that mode is commented out/TODO, see SyncMode.cs.)
            if (personal.IsSuccess)
            {
                workflow.AddSyncPair(personal.Account!.Id, new SyncPair { LocalPath = @"C:\Users\me\Photos", RemotePath = "/Photos", Mode = SyncMode.FullSync });
            }

            var settings = settingsStore.Load();

            var checks = new List<(string Name, bool Ok)>
            {
                ("first account added", personal.IsSuccess),
                ("second account added", work.IsSuccess),
                ("two accounts in settings", settings.Accounts.Count == 2),
                ("accounts have distinct ids", personal.Account!.Id != work.Account!.Id),
                ("each account has its own secret file", secretStore.HasSecrets(personal.Account!.Id) && secretStore.HasSecrets(work.Account!.Id)),
                ("two secret files on disk", Directory.GetFiles(paths.SecretsDirectory, "*.bin").Length == 2),
                ("extra folder added to first account", settings.Accounts.First(a => a.Id == personal.Account!.Id).SyncPairs.Count == 2),
                ("first account session resumable", secretStore.LoadSecrets(personal.Account!.Id)?.HasResumableSession == true),
                ("second account stores its own key password", secretStore.LoadSecrets(work.Account!.Id)?.DataPassword == "tr0ub4dor&3"),
            };

            // On-disk blobs must be encrypted: neither plaintext key password should appear.
            var allBytes = Directory.GetFiles(paths.SecretsDirectory, "*.bin")
                .Select(File.ReadAllBytes)
                .Select(b => Encoding.UTF8.GetString(b))
                .ToList();
            checks.Add(("on-disk blobs encrypted (no plaintext key passwords)",
                allBytes.All(t => !t.Contains("correct horse") && !t.Contains("tr0ub4dor"))));

            // Removing an account clears its secret and its config.
            workflow.RemoveAccount(work.Account!.Id);
            var afterRemove = settingsStore.Load();
            checks.Add(("account removed from settings", afterRemove.Accounts.Count == 1));
            checks.Add(("removed account's secret cleared", !secretStore.HasSecrets(work.Account!.Id)));

            var allOk = true;
            foreach (var (name, ok) in checks)
            {
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
                allOk &= ok;
            }

            Console.WriteLine();
            Console.WriteLine(allOk ? "SELF-TEST PASSED" : "SELF-TEST FAILED");
            return allOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELF-TEST ERROR: {ex}");
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    /// <summary>A synthetic browser-style session for tests (mirrors what the fork flow yields).</summary>
    private static ProtonSession FakeSession(string email, string keyPassword) => new()
    {
        SessionId = "sess-" + Guid.NewGuid().ToString("n"),
        UserId = "user-" + Guid.NewGuid().ToString("n"),
        Username = email,
        AccessToken = "at-" + Guid.NewGuid().ToString("n"),
        RefreshToken = "rt-" + Guid.NewGuid().ToString("n"),
        Scopes = ["drive"],
        PasswordMode = "web",
        DataPassword = keyPassword,
    };

    // ---------------------------------------------------------------- console helpers ----

    private static void Header()
    {
        Console.WriteLine("PAWS - Proton-Aware Windows Sync . setup");
        Console.WriteLine("========================================");
    }
}
