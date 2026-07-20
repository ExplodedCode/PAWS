namespace PAWS.Core.Proton;

/// <summary>A sign-in challenge for the UI to present: open <see cref="Url"/> in the browser.</summary>
public sealed record WebSignInChallenge(string Url, string UserCode);

/// <summary>
/// Browser-based ("sign in with browser") Proton authentication via session forking. The user
/// authenticates on Proton's website — password, 2FA, passkeys, and CAPTCHA are all handled there —
/// and the app receives a forked session. The user's password never passes through the app, and
/// because the actual login happens on Proton's first-party site it avoids the anti-abuse blocks
/// that a third-party doing raw SRP can trigger.
/// </summary>
public interface IWebProtonAuthenticator
{
    /// <param name="onChallengeReady">
    /// Invoked once the sign-in URL is ready; the caller opens it in a browser and tells the user to
    /// finish signing in. The method then polls until the session is forked back (or it times out).
    /// </param>
    Task<ProtonAuthResult> SignInAsync(Func<WebSignInChallenge, Task> onChallengeReady, CancellationToken cancellationToken = default);
}
