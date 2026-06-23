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
        if (secretStore.HasProtonSecrets)
        {
            Console.WriteLine("\nThis machine is already linked to a Proton account.");
            if (!Confirm("Reconfigure and overwrite the stored credentials?", defaultYes: false))
            {
                Console.WriteLine("Left unchanged.");
                return 0;
            }
        }

        Console.WriteLine("\n— Proton account —");
        var username = Prompt("Proton email");
        var password = ReadSecret("Proton password");

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
        var result = await workflow.AuthenticateAndPersistAsync(login, pair).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            Console.WriteLine($"\n  x {result.Status}: {result.Message}");
            Console.WriteLine("Nothing was saved.");
            return 1;
        }

        Console.WriteLine("  + Authenticated.");
        Console.WriteLine($"  + Credentials encrypted (DPAPI) -> {paths.ProtonSecretsFile}");
        Console.WriteLine($"  + Settings written -> {paths.SettingsFile}");

        // Prove the round-trip: reload what we just stored.
        VerifyRoundTrip(settingsStore, secretStore);

        Console.WriteLine("\nNote: this build uses the STUB authenticator (no real Proton call yet).");
        Console.WriteLine("It validates and exercises secure storage so the pipeline is ready for the real SDK.");
        return 0;
    }

    private static void VerifyRoundTrip(ISettingsStore settingsStore, ISecretStore secretStore)
    {
        Console.WriteLine("\nVerifying stored data can be read back…");
        var settings = settingsStore.Load();
        var secrets = secretStore.LoadProtonSecrets();

        Console.WriteLine($"  account        : {settings.AccountEmail}");
        Console.WriteLine($"  setup complete : {settings.SetupCompleted}");
        foreach (var p in settings.SyncPairs)
        {
            Console.WriteLine($"  sync pair      : \"{p.LocalPath}\"  <=>  \"{p.RemotePath}\"  [{p.Mode}]");
        }

        Console.WriteLine($"  session token  : {Redact(secrets?.AccessToken)}");
        Console.WriteLine($"  refresh token  : {Redact(secrets?.RefreshToken)}");
        Console.WriteLine($"  data password  : {Redact(secrets?.DataPassword)} (decrypted OK)");
    }

    // ---------------------------------------------------------------- other commands ----

    private static int Show(PawsPaths paths)
    {
        var settings = new JsonSettingsStore(paths).Load();
        var secretStore = new DpapiSecretStore(paths);

        Header();
        Console.WriteLine($"Config folder : {paths.Root}");
        Console.WriteLine($"Account       : {settings.AccountEmail ?? "(none)"}");
        Console.WriteLine($"Setup done    : {settings.SetupCompleted}");
        Console.WriteLine($"Secrets stored: {secretStore.HasProtonSecrets}");
        Console.WriteLine($"Sync pairs    : {settings.SyncPairs.Count}");
        foreach (var p in settings.SyncPairs)
        {
            Console.WriteLine($"  - \"{p.LocalPath}\"  <=>  \"{p.RemotePath}\"  [{p.Mode}]  enabled={p.Enabled}");
        }

        return 0;
    }

    private static int Reset(PawsPaths paths)
    {
        new DpapiSecretStore(paths).ClearProtonSecrets();
        if (File.Exists(paths.SettingsFile))
        {
            File.Delete(paths.SettingsFile);
        }

        Console.WriteLine("Cleared stored credentials and settings.");
        return 0;
    }

    private static int Help()
    {
        Header();
        Console.WriteLine("Usage: PAWS.Setup [command]\n");
        Console.WriteLine("  (no args)    Interactive setup: capture credentials + folder pair, store securely.");
        Console.WriteLine("  --show       Show current configuration (secrets redacted).");
        Console.WriteLine("  --reset      Delete stored credentials and settings.");
        Console.WriteLine("  --selftest   Non-interactive storage round-trip check (uses a temp folder).");
        return 0;
    }

    // ---------------------------------------------------------------- self test ----

    /// <summary>
    /// Exercises the whole persistence pipeline without user input: authenticate (stub) -> save ->
    /// reload -> assert equality. Uses a throwaway temp folder so it never touches real config.
    /// Returns 0 on success, non-zero on failure (suitable for CI).
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

            var login = new ProtonLoginRequest { Username = "tester@proton.me", Password = "correct horse battery staple" };
            var pair = new SyncPair { LocalPath = @"C:\Users\tester\ProtonSync", RemotePath = "/Backup/Desktop", Mode = SyncMode.OnDemand };

            var result = workflow.AuthenticateAndPersistAsync(login, pair).GetAwaiter().GetResult();

            var checks = new List<(string Name, bool Ok)>
            {
                ("authentication succeeded", result.IsSuccess),
                ("secret file written", secretStore.HasProtonSecrets),
                ("settings file written", File.Exists(paths.SettingsFile)),
            };

            var secrets = secretStore.LoadProtonSecrets();
            var settings = settingsStore.Load();

            checks.Add(("secrets decrypt + deserialize", secrets is not null));
            checks.Add(("username round-trips", secrets?.Username == login.Username));
            checks.Add(("data password round-trips", secrets?.DataPassword == login.Password));
            checks.Add(("session is resumable", secrets?.HasResumableSession == true));
            checks.Add(("account saved in settings", settings.AccountEmail == login.Username));
            checks.Add(("setup marked complete", settings.SetupCompleted));
            checks.Add(("sync pair saved", settings.SyncPairs.Count == 1 && settings.SyncPairs[0].RemotePath == "/Backup/Desktop"));

            // The secret blob on disk must be encrypted — assert the plaintext password is not present.
            var raw = File.ReadAllBytes(paths.ProtonSecretsFile);
            var rawText = Encoding.UTF8.GetString(raw);
            checks.Add(("on-disk blob is encrypted (no plaintext password)", !rawText.Contains("correct horse")));

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

    private static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(none)";
        }

        return value.Length <= 8
            ? new string('*', value.Length)
            : value[..4] + new string('*', value.Length - 8) + value[^4..];
    }
}
