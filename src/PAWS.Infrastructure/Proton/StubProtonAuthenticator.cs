using PAWS.Core.Proton;

namespace PAWS.Infrastructure.Proton;

/// <summary>
/// Placeholder authenticator so the setup workflow and secure storage can be exercised end-to-end
/// BEFORE the native Proton SDK (Proton.Cryptography / Proton.Sdk) is built and wired in.
/// It performs NO network calls: it validates that inputs look reasonable and returns a fake,
/// clearly-marked resumable session.
/// <para>
/// Replace with a real adapter in the planned PAWS.Proton project. The real flow is roughly:
/// </para>
/// <code>
/// var options = /* ProtonClientOptions with SecretsCache + LoggerFactory */;
/// var session = await ProtonApiSession.BeginAsync(
///     new SessionBeginRequest { Username = request.Username, Password = request.Password, Options = options },
///     cancellationToken);
///
/// if (session.IsWaitingForSecondFactorCode)
///     await session.ApplySecondFactorCodeAsync(request.TwoFactorCode!, cancellationToken);
///
/// if (session.PasswordMode == PasswordMode.Dual)            // two-password account
///     await session.ApplyDataPasswordAsync(Encoding.UTF8.GetBytes(request.MailboxPassword!), cancellationToken);
///
/// return ProtonAuthResult.Success(new ProtonSession
/// {
///     SessionId    = session.SessionId.Value,
///     UserId       = session.UserId.Value,
///     Username     = session.Username,
///     AccessToken  = session.TokenCredential.AccessToken,
///     RefreshToken = session.TokenCredential.RefreshToken,
///     Scopes       = session.Scopes.ToArray(),
///     PasswordMode = session.PasswordMode.ToString(),
/// });
/// </code>
/// </summary>
public sealed class StubProtonAuthenticator : IProtonAuthenticator
{
    public Task<ProtonAuthResult> AuthenticateAsync(ProtonLoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Username) || !request.Username.Contains('@'))
        {
            return Task.FromResult(ProtonAuthResult.Failure(
                ProtonAuthStatus.InvalidCredentials, "Username must be a Proton email address."));
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return Task.FromResult(ProtonAuthResult.Failure(
                ProtonAuthStatus.InvalidCredentials, "Password is required."));
        }

        var session = new ProtonSession
        {
            SessionId = "stub-session-" + Guid.NewGuid().ToString("n"),
            UserId = "stub-user",
            Username = request.Username,
            AccessToken = "stub-access-" + Guid.NewGuid().ToString("n"),
            RefreshToken = "stub-refresh-" + Guid.NewGuid().ToString("n"),
            Scopes = new[] { "drive" },
            PasswordMode = string.IsNullOrEmpty(request.MailboxPassword) ? "single" : "dual",
        };

        return Task.FromResult(ProtonAuthResult.Success(session));
    }
}
