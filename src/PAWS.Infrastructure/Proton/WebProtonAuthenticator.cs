using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PAWS.Core.Proton;

namespace PAWS.Infrastructure.Proton;

/// <summary>
/// Real browser-based Proton authentication via session forking (the "sign in with browser" flow).
/// Pure .NET — HTTP + <see cref="AesGcm"/> — so it needs no native crypto. Mirrors Proton's own
/// (MIT) reference flow: init a fork, open Proton's web login, poll for the forked session, then
/// AES-256-GCM-decrypt the payload to recover the key password.
/// </summary>
public sealed class WebProtonAuthenticator : IWebProtonAuthenticator
{
    private const string AppVersion = "external-drive-paws@1.0.0-stable";
    private const string AuthClientId = "external-drive"; // third-party fork client id

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxPollTime = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _apiBaseUrl;
    private readonly string _accountBaseUrl;

    public WebProtonAuthenticator(HttpClient? httpClient = null, string? apiBaseUrl = null, string? accountBaseUrl = null)
    {
        _http = httpClient ?? new HttpClient();
        _apiBaseUrl = (apiBaseUrl ?? "https://drive-api.proton.me").TrimEnd('/');
        _accountBaseUrl = (accountBaseUrl ?? "https://account.proton.me").TrimEnd('/');
    }

    public async Task<ProtonAuthResult> SignInAsync(Func<WebSignInChallenge, Task> onChallengeReady, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1) Start a session fork (unauthenticated; just the app-version header).
            var init = await GetAsync<ForkInitResponse>("/auth/v4/sessions/forks", uid: null, accessToken: null, cancellationToken).ConfigureAwait(false);
            if (init is null || string.IsNullOrEmpty(init.Selector) || string.IsNullOrEmpty(init.UserCode))
            {
                return ProtonAuthResult.Failure(ProtonAuthStatus.Error, "Could not start the sign-in (fork init failed).");
            }

            // 2) Build the Proton web login URL carrying our one-time AES key.
            var aesKey = RandomNumberGenerator.GetBytes(32);
            var payload = $"0:{init.UserCode}:{Convert.ToBase64String(aesKey)}:{AuthClientId}";
            var url = $"{_accountBaseUrl}/desktop/login?app=drive&pv=3#payload={Uri.EscapeDataString(payload)}";

            await onChallengeReady(new WebSignInChallenge(url, init.UserCode)).ConfigureAwait(false);

            // 3) Poll until the user finishes signing in on the website (422 = not ready yet).
            await Task.Delay(InitialDelay, cancellationToken).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + MaxPollTime;
            ForkStatusResponse status;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline)
                {
                    return ProtonAuthResult.Failure(ProtonAuthStatus.Error, "Sign-in timed out. Please try again.");
                }

                var (httpStatus, body) = await TryGetForkStatusAsync(init.Selector, cancellationToken).ConfigureAwait(false);
                if (httpStatus == HttpStatusCode.OK && body is not null)
                {
                    status = body;
                    break;
                }

                if ((int)httpStatus == 422)
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return ProtonAuthResult.Failure(ProtonAuthStatus.Error, $"Sign-in failed (HTTP {(int)httpStatus}).");
            }

            if (string.IsNullOrEmpty(status.UID) || string.IsNullOrEmpty(status.AccessToken))
            {
                return ProtonAuthResult.Failure(ProtonAuthStatus.Error, "Sign-in returned an incomplete session.");
            }

            // 4) Recover the key password from the encrypted fork payload.
            var keyPassword = DecryptForkPayload(status.Payload, aesKey);

            // 5) Best-effort: resolve the account's user id + email for display.
            var (userId, email) = await TryGetIdentityAsync(status.UID, status.AccessToken, cancellationToken).ConfigureAwait(false);

            var session = new ProtonSession
            {
                SessionId = status.UID,
                UserId = userId ?? status.UID,
                Username = email ?? "(unknown)",
                AccessToken = status.AccessToken,
                RefreshToken = status.RefreshToken ?? string.Empty,
                Scopes = Array.Empty<string>(),
                PasswordMode = "web",
                DataPassword = keyPassword,
            };

            return ProtonAuthResult.Success(session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProtonAuthResult.Failure(ProtonAuthStatus.Error, ex.Message);
        }
    }

    private async Task<(HttpStatusCode Status, ForkStatusResponse? Body)> TryGetForkStatusAsync(string selector, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBaseUrl}/auth/v4/sessions/forks/{Uri.EscapeDataString(selector)}");
        AddHeaders(req, uid: null, accessToken: null);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadFromJsonAsync<ForkStatusResponse>(JsonOptions, ct).ConfigureAwait(false);
            return (HttpStatusCode.OK, body);
        }

        return (resp.StatusCode, null);
    }

    private async Task<T?> GetAsync<T>(string path, string? uid, string? accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBaseUrl}{path}");
        AddHeaders(req, uid, accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
    }

    private static void AddHeaders(HttpRequestMessage req, string? uid, string? accessToken)
    {
        req.Headers.TryAddWithoutValidation("x-pm-appversion", AppVersion);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.protonmail.api+json");
        req.Headers.TryAddWithoutValidation("User-Agent", "PAWS/1.0");
        if (!string.IsNullOrEmpty(uid))
        {
            req.Headers.TryAddWithoutValidation("x-pm-uid", uid);
        }

        if (!string.IsNullOrEmpty(accessToken))
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        }
    }

    private async Task<(string? UserId, string? Email)> TryGetIdentityAsync(string uid, string accessToken, CancellationToken ct)
    {
        try
        {
            var users = await GetAsync<UsersResponse>("/core/v4/users", uid, accessToken, ct).ConfigureAwait(false);
            var addresses = await GetAsync<AddressesResponse>("/core/v4/addresses?Page=0&PageSize=1", uid, accessToken, ct).ConfigureAwait(false);
            var email = addresses?.Addresses?.FirstOrDefault()?.Email;
            return (users?.User?.ID, email);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string DecryptForkPayload(string base64Payload, byte[] key)
    {
        var blob = Convert.FromBase64String(base64Payload);
        const int nonceLength = 12;
        const int tagLength = 16;
        if (blob.Length < nonceLength + tagLength)
        {
            throw new InvalidOperationException("Invalid fork payload length.");
        }

        var nonce = blob.AsSpan(0, nonceLength);
        var tag = blob.AsSpan(blob.Length - tagLength, tagLength);
        var ciphertext = blob.AsSpan(nonceLength, blob.Length - nonceLength - tagLength);
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new AesGcm(key, tagLength))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, "fork"u8);
        }

        using var doc = JsonDocument.Parse(plaintext);
        return doc.RootElement.GetProperty("keyPassword").GetString()
            ?? throw new InvalidOperationException("Fork payload missing keyPassword.");
    }

    private sealed record ForkInitResponse
    {
        [JsonPropertyName("Selector")] public string? Selector { get; init; }
        [JsonPropertyName("UserCode")] public string? UserCode { get; init; }
    }

    private sealed record ForkStatusResponse
    {
        [JsonPropertyName("UID")] public string? UID { get; init; }
        [JsonPropertyName("AccessToken")] public string? AccessToken { get; init; }
        [JsonPropertyName("RefreshToken")] public string? RefreshToken { get; init; }
        [JsonPropertyName("Payload")] public string Payload { get; init; } = string.Empty;
    }

    private sealed record UsersResponse
    {
        [JsonPropertyName("User")] public UserDto? User { get; init; }
    }

    private sealed record UserDto
    {
        [JsonPropertyName("ID")] public string? ID { get; init; }
    }

    private sealed record AddressesResponse
    {
        [JsonPropertyName("Addresses")] public List<AddressDto>? Addresses { get; init; }
    }

    private sealed record AddressDto
    {
        [JsonPropertyName("Email")] public string? Email { get; init; }
    }
}
