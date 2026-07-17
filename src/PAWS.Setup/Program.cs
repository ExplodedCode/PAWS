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
        return 0;
    }

    // ---------------------------------------------------------------- console helpers ----

    private static void Header()
    {
        Console.WriteLine("PAWS - Proton-Aware Windows Sync . setup");
        Console.WriteLine("========================================");
    }
}
