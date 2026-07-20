using System.Reflection;
using PAWS.Core.Security;
using PAWS.Proton.Drive;

namespace PAWS.Tests;

/// <summary>
/// Offline coverage (no Proton, no network) for <see cref="ProtonDriveClientFactory"/>'s refresh-token-
/// expiry handling. Reflects into the private HandleSessionExpired method — the SAME code CreateAsync
/// wires to the SDK's TokenCredential.RefreshTokenExpired event, not a reimplementation; it's private
/// only to avoid exposing internal plumbing publicly, and a real end-to-end trigger needs an actual dead
/// refresh token rejected by Proton's server, which isn't safe to provoke against a real account. Ported
/// from PAWS.AuthTest's --reauthtest.
/// </summary>
public class ProtonAuthReauthTests
{
    private const string AccountId = "acct-1";

    [Fact]
    public void HandleSessionExpired_ClearsTokensAndRaisesSessionExpiredOnce()
    {
        var store = new FakeSecretStore();
        store.SaveSecrets(AccountId, new ProtonSecrets
        {
            Username = "person@proton.me",
            DataPassword = "pw",
            SessionId = "sess-1",
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            UserId = "user-1",
        });

        Assert.True(store.LoadSecrets(AccountId)?.HasResumableSession);

        var factory = new ProtonDriveClientFactory(store);
        string? raisedAccountId = null;
        var raiseCount = 0;
        factory.SessionExpired += id =>
        {
            raisedAccountId = id;
            raiseCount++;
        };

        var handleExpired = typeof(ProtonDriveClientFactory).GetMethod("HandleSessionExpired", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("HandleSessionExpired not found on ProtonDriveClientFactory — did it get renamed?");
        handleExpired.Invoke(factory, [AccountId]);

        var after = store.LoadSecrets(AccountId);
        Assert.NotNull(after);
        Assert.Null(after!.AccessToken);
        Assert.Null(after.RefreshToken);
        Assert.False(after.HasResumableSession);

        Assert.Equal(1, raiseCount);
        Assert.Equal(AccountId, raisedAccountId);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateAsync(AccountId).GetAwaiter().GetResult());
        Assert.Contains("sign in again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
