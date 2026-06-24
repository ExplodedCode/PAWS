using System.Text;
using PAWS.Core.Proton;
using PAWS.Proton;

Console.OutputEncoding = Encoding.UTF8;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "login";

return mode switch
{
    "--cryptocheck" or "cryptocheck" => CryptoCheck(),
    _ => await LoginAsync(),
};

// Proves the native proton_crypto library loads and runs, without needing a Proton account.
static int CryptoCheck()
{
    try
    {
        Console.WriteLine("Generating a throwaway PGP key via the native proton_crypto library…");
        var fingerprint = ProtonCryptoSelfTest.GenerateKeyFingerprint();
        Console.WriteLine($"OK - native crypto works. Generated key fingerprint: {fingerprint}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED to load/run native crypto:\n{ex}");
        return 1;
    }
}

// Performs a REAL login against the Proton API using the official SDK.
static async Task<int> LoginAsync()
{
    Console.WriteLine("PAWS - real Proton login test\n");

    Console.Write("Proton email: ");
    var email = Console.ReadLine()?.Trim() ?? string.Empty;
    var password = ReadSecret("Proton password: ");
    Console.Write("2FA code (leave blank if 2FA is off): ");
    var twoFactor = Console.ReadLine()?.Trim();
    var mailbox = ReadSecret("Mailbox password (blank for single-password accounts): ");

    var authenticator = new ProtonAuthenticator();

    Console.WriteLine("\nAuthenticating against Proton…");
    var result = await authenticator.AuthenticateAsync(new ProtonLoginRequest
    {
        Username = email,
        Password = password,
        TwoFactorCode = string.IsNullOrEmpty(twoFactor) ? null : twoFactor,
        MailboxPassword = string.IsNullOrEmpty(mailbox) ? null : mailbox,
    });

    if (!result.IsSuccess)
    {
        Console.WriteLine($"  x {result.Status}: {result.Message}");
        return 1;
    }

    var s = result.Session!;
    Console.WriteLine("  + Authenticated against Proton!\n");
    Console.WriteLine($"    user id       : {s.UserId}");
    Console.WriteLine($"    session id    : {s.SessionId}");
    Console.WriteLine($"    password mode : {s.PasswordMode}");
    Console.WriteLine($"    scopes        : {string.Join(", ", s.Scopes)}");
    Console.WriteLine($"    access token  : {Redact(s.AccessToken)}");
    Console.WriteLine($"    refresh token : {Redact(s.RefreshToken)}");
    return 0;
}

static string ReadSecret(string label)
{
    Console.Write(label);
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

static string Redact(string? value)
    => string.IsNullOrEmpty(value)
        ? "(none)"
        : value.Length <= 8 ? new string('*', value.Length) : value[..4] + "…" + value[^4..];
