using Microsoft.Extensions.Logging;

namespace Domain.Security.Signing;

// A verifier's copy of one issuer's published keys. The fetch is the caller's: Domain makes no HTTP
// call, so a host hands in `ct => http.GetStringAsync(jwksUrl, ct)` over its own HttpClient.
//
// One fetch at a time: a gate is asked by many calls at once, and every call that the copy cannot
// answer waits for the one fetch in flight rather than starting its own.
public sealed class JwksCache(
    Func<CancellationToken, Task<string>> fetch,
    TimeProvider clock,
    TokenVerificationSettings? settings = null,
    ILogger<JwksCache>? logger = null) : IVerificationKeys
{
    private readonly TokenVerificationSettings _settings = settings ?? new TokenVerificationSettings();
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private volatile Copy? _copy;
    private DateTimeOffset? _attemptedAt;
    private Exception? _failure;

    public async ValueTask<VerificationKey?> FindAsync(string keyId, CancellationToken ct)
    {
        if (Answer(keyId) is { } known)
        {
            return known;
        }

        await _fetching.WaitAsync(ct);
        try
        {
            // Whoever held the lock before may have fetched exactly what this call is missing.
            if (Answer(keyId) is { } fetched)
            {
                return fetched;
            }

            if (MayFetch())
            {
                await RefreshAsync(ct);
            }

            return _copy is { } copy
                ? copy.Keys.GetValueOrDefault(keyId)
                : throw new SigningKeysUnavailableException("the issuer's published keys could not be fetched", _failure);
        }
        finally
        {
            _fetching.Release();
        }
    }

    // A failed fetch keeps the copy there was: an issuer restarting as a copy runs out would otherwise
    // refuse every call until it was back. The interval still holds, so the failure is not a fetch per
    // call either.
    private async Task RefreshAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        _attemptedAt = now;
        try
        {
            _copy = new Copy(Read(await fetch(ct)), now);
            _failure = null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _failure = e;
            logger?.LogWarning(e, "Fetching an issuer's published signing keys failed; {Fallback}",
                _copy is null ? "tokens are refused until it succeeds" : "the keys fetched before stay in use");
        }
    }

    // A key the copy holds, while the copy is young enough to be believed without asking.
    private VerificationKey? Answer(string keyId) =>
        _copy is { } copy && clock.GetUtcNow() - copy.FetchedAt < _settings.KeysLifetime
            ? copy.Keys.GetValueOrDefault(keyId)
            : null;

    private bool MayFetch() =>
        _attemptedAt is not { } attempted || clock.GetUtcNow() - attempted >= _settings.KeysRefetchInterval;

    private static IReadOnlyDictionary<string, VerificationKey> Read(string json) =>
        JsonWebKeySet.Parse(json).Keys
            .Select(VerificationKey.From)
            .OfType<VerificationKey>()
            .DistinctBy(k => k.Id)
            .ToDictionary(k => k.Id);

    private sealed record Copy(IReadOnlyDictionary<string, VerificationKey> Keys, DateTimeOffset FetchedAt);
}