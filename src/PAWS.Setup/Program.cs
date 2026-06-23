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

        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "setup";

        return mode switch
        {
            "--selftest" or "selftest" => SelfTest(),
            "--show" or "show" => Show(BuildPaths()),
            "--reset" or "reset" => Reset(BuildPaths()),
            "--help" or "-h" or "help" => Help(),
            _ => await RunInteractiveSetupAsync().ConfigureAwait(false),
        };
    }

    private static PawsPaths BuildPaths() => new();

    // ---------------------------------------------------------------- interactive setup ----

    private static async Task<int> RunInteractiveSetupAsync()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Interactive setup needs a real console. For automated checks use: PAWS.Setup --selftest");
            return 2;
        }

        var paths = BuildPaths();
        var settingsStore = new JsonSettingsStore(paths);
        var secretStore = new DpapiSecretStore(paths);
        IProtonAuthenticator authenticator = new StubProtonAuthenticator();
        var workflow = new SetupWorkflow(settingsStore, secretStore, authenticator);

        Header();
        Console.WriteLine($"Config folder: {paths.Root}");

        var existing = settingsStore.Load();
        if (existing.Accounts.Count > 0)
        {
            Console.WriteLine($"\n{existing.Accounts.Count} account(s) already configured. Adding another.");
        }

        Console.WriteLine("\n— Proton account —");
        var username = Prompt("Proton email");
        var password = ReadSecret("Proton password");
        var displayName = PromptOptional("Label for this account (optional, e.g. Work)");

        var mailboxPassword = Confirm("Does this account use a separate second (mailbox) password?", defaultYes: false)
            ? ReadSecret("Mailbox password")
            : null;

        var twoFactor = Confirm("Is two-factor authentication (2FA) enabled?", defaultYes: false)
            ? Prompt("Current 2FA code")
            : null;

        Console.WriteLine("\n— Folder to sync —");
        var localPath = PromptLocalFolder();
        var remotePath = PromptWithDefault("Proton Drive folder (e.g. /Backup/Desktop)", "/");
        var mode = PromptSyncMode();

        var login = new ProtonLoginRequest
        {
            Username = username,
            Password = password,
            MailboxPassword = mailboxPassword,
            TwoFactorCode = twoFactor,
        };

        var pair = new SyncPair { LocalPath = localPath, RemotePath = remotePath, Mode = mode };

        Console.WriteLine("\nAuthenticating…");
        var result = await workflow.AddAccountAsync(login, pair, displayName).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            Console.WriteLine($"\n  x {result.Auth.Status}: {result.Auth.Message}");
            Console.WriteLine("Nothing was saved.");
            return 1;
        }

        Console.WriteLine("  + Authenticated.");
        Console.WriteLine($"  + Credentials encrypted (DPAPI) -> {paths.SecretsFileFor(result.Account!.Id)}");
        Console.WriteLine($"  + Settings written -> {paths.SettingsFile}");

        Console.WriteLine();
        ShowAccounts(settingsStore, secretStore);

        Console.WriteLine("\nNote: this build uses the STUB authenticator (no real Proton call yet).");
        Console.WriteLine("Run setup again to add more accounts or more folders.");
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
        Console.WriteLine("  (no args)    Interactive setup: add a Proton account + folder, store securely.");
        Console.WriteLine("  --show       List configured accounts and their folders (secrets redacted).");
        Console.WriteLine("  --reset      Delete ALL stored credentials and settings.");
        Console.WriteLine("  --selftest   Non-interactive multi-account storage round-trip check (temp folder).");
        return 0;
    }

    // ---------------------------------------------------------------- self test ----

    /// <summary>
    /// Exercises the multi-account persistence pipeline without user input: add two accounts (incl.
    /// extra folder), reload, assert, then remove one. Uses a throwaway temp folder. Returns 0 on
    /// success, non-zero on failure (suitable for CI).
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
            var workflow = new SetupWorkflow(settingsStore, secretStore, new StubProtonAuthenticator());

            var personal = workflow.AddAccountAsync(
                new ProtonLoginRequest { Username = "personal@proton.me", Password = "correct horse battery staple" },
                new SyncPair { LocalPath = @"C:\Users\me\Personal", RemotePath = "/Personal", Mode = SyncMode.OnDemand },
                "Personal").GetAwaiter().GetResult();

            var work = workflow.AddAccountAsync(
                new ProtonLoginRequest { Username = "work@proton.me", Password = "tr0ub4dor&3" },
                new SyncPair { LocalPath = @"C:\Users\me\Work", RemotePath = "/Work", Mode = SyncMode.FullSync },
                "Work").GetAwaiter().GetResult();

            // Same account can be added again, and accounts can have multiple folders.
            if (personal.IsSuccess)
            {
                workflow.AddSyncPair(personal.Account!.Id, new SyncPair { LocalPath = @"C:\Users\me\Photos", RemotePath = "/Photos", Mode = SyncMode.CloudOnly });
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
                ("second account stores its own password", secretStore.LoadSecrets(work.Account!.Id)?.DataPassword == "tr0ub4dor&3"),
            };

            // On-disk blobs must be encrypted: neither plaintext password should appear.
            var allBytes = Directory.GetFiles(paths.SecretsDirectory, "*.bin")
                .Select(File.ReadAllBytes)
                .Select(b => Encoding.UTF8.GetString(b))
                .ToList();
            checks.Add(("on-disk blobs encrypted (no plaintext passwords)",
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

    // ---------------------------------------------------------------- console helpers ----

    private static void Header()
    {
        Console.WriteLine("PAWS - Proton-Aware Windows Sync . setup");
        Console.WriteLine("========================================");
    }

    private static string Prompt(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            Console.WriteLine("  (required)");
        }
    }

    private static string? PromptOptional(string label)
    {
        Console.Write($"{label}: ");
        var value = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string PromptWithDefault(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        var value = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }

    private static string PromptLocalFolder()
    {
        while (true)
        {
            var path = Prompt(@"Local Windows folder (e.g. C:\Users\you\ProtonSync)");
            if (Directory.Exists(path))
            {
                return path;
            }

            if (Confirm($"\"{path}\" does not exist. Create it?", defaultYes: true))
            {
                try
                {
                    Directory.CreateDirectory(path);
                    return path;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Could not create folder: {ex.Message}");
                }
            }
        }
    }

    private static SyncMode PromptSyncMode()
    {
        Console.WriteLine("Sync mode:");
        Console.WriteLine("  1) On-demand  - cloud files appear as placeholders, downloaded when opened (default)");
        Console.WriteLine("  2) Full sync  - keep every file fully local and in the cloud");
        Console.WriteLine("  3) Cloud-only - keep files in the cloud, download only when pinned");
        Console.Write("Choice [1]: ");
        return (Console.ReadLine()?.Trim()) switch
        {
            "2" => SyncMode.FullSync,
            "3" => SyncMode.CloudOnly,
            _ => SyncMode.OnDemand,
        };
    }

    private static bool Confirm(string question, bool defaultYes)
    {
        Console.Write($"{question} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        var value = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return defaultYes;
        }

        return value is "y" or "yes";
    }

    private static string ReadSecret(string label)
    {
        Console.Write($"{label}: ");
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return builder.ToString();
                case ConsoleKey.Backspace when builder.Length > 0:
                    builder.Length--;
                    Console.Write("\b \b");
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        builder.Append(key.KeyChar);
                        Console.Write('*');
                    }

                    break;
            }
        }
    }
}
