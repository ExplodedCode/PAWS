using System.Text;
using Proton.Sdk.Caching;

namespace PAWS.Proton;

/// <summary>
/// Secret cache for a browser/session-fork login. The fork hands us the already-derived
/// <c>keyPassword</c> (the passphrase that unlocks the user's PGP keys), so — unlike the SRP/password
/// flow — there is no raw password to bcrypt against per-key salts. The SDK normally populates that
/// via <c>ApplyDataPasswordAsync</c>, which fetches <c>/keys/salts</c>; an external-drive session is
/// forbidden from reading salts (HTTP 403), and re-deriving would be wrong anyway.
///
/// Instead we answer every account-key-passphrase lookup (<c>account:passphrase:{keyId}</c>, the
/// SDK's <c>SessionSecretCache</c> key shape) with the fork's keyPassword. That covers active user
/// keys (and legacy address keys locked directly by the key password); modern address keys unlock via
/// the user keys and never hit this path. All other cache entries pass through to a normal in-memory
/// store.
/// </summary>
internal sealed class ForkSecretCacheRepository : ICacheRepository
{
    // Matches Proton.Sdk.Caching.SessionSecretCache.GetAccountPassphraseCacheKey(keyId).
    private const string AccountPassphrasePrefix = "account:passphrase:";

    private readonly ICacheRepository _inner = new InMemoryCacheRepository();
    private readonly string _passphraseBase64;

    public ForkSecretCacheRepository(string keyPassword)
        => _passphraseBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(keyPassword));

    public ValueTask<string?> TryGetAsync(string key, CancellationToken cancellationToken)
        => key.StartsWith(AccountPassphrasePrefix, StringComparison.Ordinal)
            ? new ValueTask<string?>(_passphraseBase64)
            : _inner.TryGetAsync(key, cancellationToken);

    public ValueTask SetAsync(string key, string value, IEnumerable<string> tags, CancellationToken cancellationToken)
        => _inner.SetAsync(key, value, tags, cancellationToken);

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
        => _inner.RemoveAsync(key, cancellationToken);

    public ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken)
        => _inner.RemoveByTagAsync(tag, cancellationToken);

    public ValueTask ClearAsync() => _inner.ClearAsync();

    public IAsyncEnumerable<(string Key, string Value)> GetByTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
        => _inner.GetByTagsAsync(tags, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
