using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PAWS.Core.Proton;
using Proton.Sdk;
using Proton.Sdk.Cryptography;

namespace PAWS.Proton;

/// <summary>
/// Real Proton authenticator backed by the official <c>Proton.Sdk</c> (<see cref="ProtonApiSession"/>)
/// and <c>Proton.Cryptography</c> (GopenPGP/GoSRP via the native <c>proton_crypto</c> library).
/// Implements the same <see cref="IProtonAuthenticator"/> port as the stub, so it drops straight into
/// the existing setup workflow.
/// </summary>
public sealed class ProtonAuthenticator : IProtonAuthenticator
{
    // Proton's API validates the client version (x-pm-appversion header). Third-party Drive clients use
    // the "external-drive-<name>@<x.y.z>-stable" convention the Proton servers accept (same scheme rclone uses).
    private const string DefaultAppVersion = "external-drive-paws@1.0.0-stable";

    private readonly string _appVersion;

    public ProtonAuthenticator(string? appVersion = null)
        => _appVersion = string.IsNullOrWhiteSpace(appVersion) ? DefaultAppVersion : appVersion;

    public async Task<ProtonAuthResult> AuthenticateAsync(ProtonLoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var options = new ProtonClientOptions { AppVersion = _appVersion };
            options.SecretsCache = new InMemorySecretsCache(NullLogger<InMemorySecretsCache>.Instance);

            var beginRequest = new SessionBeginRequest
            {
                Username = request.Username,
                Password = request.Password,
                Options = options,
            };

            // SRP-6a handshake against the Proton API (uses the native crypto under the hood).
            var session = await ProtonApiSession.BeginAsync(beginRequest, cancellationToken).ConfigureAwait(false);

            if (session.IsWaitingForSecondFactorCode)
            {
                if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
                {
                    return ProtonAuthResult.Failure(ProtonAuthStatus.TwoFactorRequired, "This account requires a 2FA code.");
                }

                await session.ApplySecondFactorCodeAsync(request.TwoFactorCode!, cancellationToken).ConfigureAwait(false);
            }

            // Unlock the encryption keys with the data/mailbox password. In single-password accounts the
            // login password is used. Best effort: key correctness is validated later when keys are used.
            var dataPassword = string.IsNullOrEmpty(request.MailboxPassword) ? request.Password : request.MailboxPassword!;
            try
            {
                await session.ApplyDataPasswordAsync(Encoding.UTF8.GetBytes(dataPassword), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal for obtaining the authenticated session.
            }

            var (accessToken, refreshToken) = await session.TokenCredential.GetTokensAsync(cancellationToken).ConfigureAwait(false);

            var protonSession = new ProtonSession
            {
                SessionId = session.SessionId.Value,
                UserId = session.UserId.Value,
                Username = session.Username,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                Scopes = session.Scopes.ToArray(),
                PasswordMode = session.PasswordMode.ToString(),
            };

            return ProtonAuthResult.Success(protonSession);
        }
        catch (ProtonApiException ex)
        {
            // Only 8002 is truly "wrong credentials". Other codes — e.g. 2028 (anti-abuse / human
            // verification required) or account-disabled — are NOT credential errors, so surface
            // the server's own message instead of mislabeling it.
            var status = ex.Code == ResponseCode.IncorrectLoginCredentials
                ? ProtonAuthStatus.InvalidCredentials
                : ProtonAuthStatus.Error;
            return ProtonAuthResult.Failure(status, ex.Message);
        }
        catch (Exception ex)
        {
            return ProtonAuthResult.Failure(ProtonAuthStatus.Error, ex.Message);
        }
    }
}
