using Domain.Security.Signing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Security.Signing;

// A verifier's copy of an issuer's published keys: fetched when first needed, served from memory
// after that, and fetched again when a token names a key the copy does not hold — which is how a
// rotated key is picked up without a restart — but never more often than the refetch interval
// allows, whatever a caller sends.
public class JwksCacheTests
{
    private const string Audience = "memory";
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(2);
    private static readonly TokenVerificationSettings _settings = new();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly SigningKeyStore _current = TestSigningKeys.Store(TestSigningKeys.Agent);
    private readonly SigningKeyStore _next = TestSigningKeys.Store(TestSigningKeys.Accounts);
    private readonly Issuer _issuer;

    public JwksCacheTests()
    {
        _issuer = new Issuer(_current.PublishedKeys);
    }

    private TokenVerifier VerifierOver(JwksCache cache) => new(TokenIssuers.Agent, cache, _clock, _settings);

    private JwksCache Cache() => new(_issuer.FetchAsync, _clock, _settings);

    private string Token(SigningKeyStore keys) =>
        new TokenSigner(keys).Sign(IdentityClaims.Person("0123456789abcdef0123456789abcdef", "home", "signalr", [])
            .Stamped(TokenIssuers.Agent, Audience, _clock.GetUtcNow(), _lifetime));

    [Fact]
    public async Task TheKeys_AreFetchedOnceAndLaterChecksAreServedFromTheCopy()
    {
        var verifier = VerifierOver(Cache());

        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(30));
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();

        _issuer.Fetches.ShouldBe(1);
    }

    // The issuer rotated: it signs with a key the copy has never seen, and publishes it beside the
    // one before. The first token naming it is the verifier's cue to fetch again.
    [Fact]
    public async Task ATokenNamingAKeyTheCopyLacks_IsVerifiedAfterOneRefetch()
    {
        var verifier = VerifierOver(Cache());
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();

        _issuer.Published = new JsonWebKeySet([.. _current.PublishedKeys.Keys, .. _next.PublishedKeys.Keys]);
        _clock.Advance(_settings.KeysRefetchInterval);
        var verification = await verifier.VerifyAsync(Token(_next), Audience);

        verification.IsVerified.ShouldBeTrue();
        _issuer.Fetches.ShouldBe(2);
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();
        _issuer.Fetches.ShouldBe(2);
    }

    // Anyone can make up a key id; only one fetch per interval comes of it.
    [Fact]
    public async Task UnknownKeysWithinTheRefetchInterval_CauseNoFurtherFetch()
    {
        var verifier = VerifierOver(Cache());
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();

        _clock.Advance(_settings.KeysRefetchInterval - TimeSpan.FromSeconds(1));
        (await verifier.VerifyAsync(Token(_next), Audience)).Refusal.ShouldBe(TokenRefusal.UnknownKey);
        (await verifier.VerifyAsync(Token(_next), Audience)).Refusal.ShouldBe(TokenRefusal.UnknownKey);
        _issuer.Fetches.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await verifier.VerifyAsync(Token(_next), Audience)).Refusal.ShouldBe(TokenRefusal.UnknownKey);
        (await verifier.VerifyAsync(Token(_next), Audience)).Refusal.ShouldBe(TokenRefusal.UnknownKey);
        _issuer.Fetches.ShouldBe(2);
    }

    // A server's gate is asked by many calls at once; when none of them can be answered from the copy,
    // one fetch answers them all.
    [Fact]
    public async Task ConcurrentChecksNeedingTheKeys_CauseOneFetch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var issuer = new Issuer(_current.PublishedKeys) { Gate = release.Task };
        var verifier = VerifierOver(new JwksCache(issuer.FetchAsync, _clock, _settings));
        var token = Token(_current);

        var checks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => verifier.VerifyAsync(token, Audience))).ToList();
        await Eventually.Until(() => issuer.Fetches >= 1, "the first check to start fetching");
        await Eventually.Settle();
        release.SetResult();
        var verifications = await Task.WhenAll(checks);

        verifications.ShouldAllBe(v => v.IsVerified);
        issuer.Fetches.ShouldBe(1);
    }

    // A retired key is believed only as long as a copy that still lists it lives: once that has run
    // out the verifier asks again and learns the key is gone. The operator's wait between rotating
    // and retiring is this lifetime plus the longest token's.
    [Fact]
    public async Task ARetiredKey_IsRefusedOnceTheCopyHasLivedItsLifetime()
    {
        var verifier = VerifierOver(Cache());
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();

        _issuer.Published = _next.PublishedKeys;
        _clock.Advance(_settings.KeysLifetime - TimeSpan.FromSeconds(1));
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();
        _issuer.Fetches.ShouldBe(1);

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await verifier.VerifyAsync(Token(_current), Audience)).Refusal.ShouldBe(TokenRefusal.UnknownKey);
        _issuer.Fetches.ShouldBe(2);
    }

    // An issuer that cannot be reached is not an issuer that holds no such key: the gate reports
    // which, because one is a network to go and look at and the other a token to distrust.
    [Fact]
    public async Task AnIssuerThatCannotBeReached_IsRefusedAsKeysUnavailable()
    {
        _issuer.Failure = new HttpRequestException("connection refused");
        var verifier = VerifierOver(Cache());

        var verification = await verifier.VerifyAsync(Token(_current), Audience);

        verification.Refusal.ShouldBe(TokenRefusal.KeysUnavailable);
    }

    // An issuer restarting while a copy runs out does not refuse every call in the meantime: the copy
    // it already had keeps answering until a fetch succeeds, and the failure does not turn into a
    // fetch per call.
    [Fact]
    public async Task AFailedRefetch_KeepsTheCopyItHadAndWaitsOutTheInterval()
    {
        var verifier = VerifierOver(Cache());
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();

        _issuer.Failure = new HttpRequestException("connection refused");
        _clock.Advance(_settings.KeysLifetime);
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();
        (await verifier.VerifyAsync(Token(_current), Audience)).IsVerified.ShouldBeTrue();

        _issuer.Fetches.ShouldBe(2);
    }

    // The issuer's JWKS endpoint, as a verifier's fetch sees it: whatever it publishes right now.
    private sealed class Issuer(JsonWebKeySet published)
    {
        private int _fetches;

        public JsonWebKeySet Published { get; set; } = published;

        public Exception? Failure { get; set; }

        // What an answer waits on, so a test can hold a fetch in flight.
        public Task Gate { get; init; } = Task.CompletedTask;

        public int Fetches => Volatile.Read(ref _fetches);

        public async Task<string> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _fetches);
            await Gate.WaitAsync(ct);
            return Failure is { } failure ? throw failure : Published.ToJson();
        }
    }
}