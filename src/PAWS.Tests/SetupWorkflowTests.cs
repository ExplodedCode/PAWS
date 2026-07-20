using System.Text;
using PAWS.Core.Configuration;
using PAWS.Core.Proton;
using PAWS.Core.Setup;
using PAWS.Infrastructure.Proton;
using PAWS.Infrastructure.Storage;

namespace PAWS.Tests;

/// <summary>
/// Exercises the multi-account persistence pipeline (add two accounts incl. an extra folder, reload,
/// assert, then remove one) against real DPAPI-backed storage in a throwaway temp folder. Uses synthetic
/// browser-style sessions, no network. Ported from PAWS.Setup's --selftest.
/// </summary>
public class SetupWorkflowTests
{
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

    [Fact]
    public void AddReloadRemove_RoundTripsCorrectlyThroughEncryptedStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "PAWS_setuptest_" + Guid.NewGuid().ToString("n"));
        var paths = new PawsPaths(root);
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

            Assert.True(personal.IsSuccess);
            Assert.True(work.IsSuccess);

            // Same account can be added again, and accounts can have multiple folders.
            workflow.AddSyncPair(personal.Account!.Id, new SyncPair { LocalPath = @"C:\Users\me\Photos", RemotePath = "/Photos", Mode = SyncMode.FullSync });

            var settings = settingsStore.Load();

            Assert.Equal(2, settings.Accounts.Count);
            Assert.NotEqual(personal.Account!.Id, work.Account!.Id);
            Assert.True(secretStore.HasSecrets(personal.Account!.Id));
            Assert.True(secretStore.HasSecrets(work.Account!.Id));
            Assert.Equal(2, Directory.GetFiles(paths.SecretsDirectory, "*.bin").Length);
            Assert.Equal(2, settings.Accounts.First(a => a.Id == personal.Account!.Id).SyncPairs.Count);
            Assert.True(secretStore.LoadSecrets(personal.Account!.Id)?.HasResumableSession);
            Assert.Equal("tr0ub4dor&3", secretStore.LoadSecrets(work.Account!.Id)?.DataPassword);

            // On-disk blobs must be encrypted: neither plaintext key password should appear.
            var allBytes = Directory.GetFiles(paths.SecretsDirectory, "*.bin")
                .Select(File.ReadAllBytes)
                .Select(b => Encoding.UTF8.GetString(b))
                .ToList();
            Assert.All(allBytes, text =>
            {
                Assert.DoesNotContain("correct horse", text, StringComparison.Ordinal);
                Assert.DoesNotContain("tr0ub4dor", text, StringComparison.Ordinal);
            });

            // Removing an account clears its secret and its config.
            workflow.RemoveAccount(work.Account!.Id);
            var afterRemove = settingsStore.Load();
            Assert.Single(afterRemove.Accounts);
            Assert.False(secretStore.HasSecrets(work.Account!.Id));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }
}
