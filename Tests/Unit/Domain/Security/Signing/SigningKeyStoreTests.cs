using Domain.Security.Signing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Security.Signing;

// An issuer's keys as its volume keeps them: generated on first start and the same key every start
// after, rotated without breaking a token signed a moment before, retired once nothing signed with
// the old key can still be in date — and, on a test stack, a fixed key handed in instead.
public sealed class SigningKeyStoreTests : IDisposable
{
    private const string Audience = "memory";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"signing-keys-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SigningKeyStore Open() => SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _directory });

    private string Token(SigningKeyStore keys) =>
        new TokenSigner(keys).Sign(IdentityClaims.Person("0123456789abcdef0123456789abcdef", "home", "signalr", [])
            .Stamped(TokenIssuers.Agent, Audience, _clock.GetUtcNow(), TimeSpan.FromMinutes(2)));

    private Task<TokenVerification<IdentityClaims>> VerifyAgainst(JsonWebKeySet published, string token) =>
        new TokenVerifier(TokenIssuers.Agent, new JwksCache(_ => Task.FromResult(published.ToJson()), _clock), _clock)
            .VerifyAsync(token, Audience);

    [Fact]
    public async Task AFirstStart_GeneratesAKeyThatEveryLaterStartSignsWith()
    {
        var first = Open();
        var token = Token(first);

        var restarted = Open();

        restarted.CurrentKeyId.ShouldBe(first.CurrentKeyId);
        restarted.PublishedKeys.Keys.Select(k => k.KeyId).ShouldBe([first.CurrentKeyId]);
        (await VerifyAgainst(restarted.PublishedKeys, token)).IsVerified.ShouldBeTrue();
    }

    // A rotation is a command run beside the issuer, not inside it: the running issuer signs with the
    // new key from its next token, and still publishes the one before, so a token it signed a moment
    // earlier keeps verifying.
    [Fact]
    public async Task ARotation_SignsWithANewKeyAndKeepsThePreviousOnePublished()
    {
        var running = Open();
        var before = Token(running);
        var previous = running.CurrentKeyId;

        Open().Rotate();
        var after = Token(running);

        running.CurrentKeyId.ShouldNotBe(previous);
        running.PublishedKeys.Keys.Select(k => k.KeyId).ShouldBe([previous, running.CurrentKeyId]);
        (await VerifyAgainst(running.PublishedKeys, before)).IsVerified.ShouldBeTrue();
        (await VerifyAgainst(running.PublishedKeys, after)).IsVerified.ShouldBeTrue();
        (await VerifyAgainst(new JsonWebKeySet([running.PublishedKeys.Keys[0]]), after)).Refusal
            .ShouldBe(TokenRefusal.UnknownKey);
    }

    // Retiring is the later command, once every copy of the JWKS and every token signed with the
    // previous key has run out: that key is no longer published, and the current one is untouched.
    [Fact]
    public async Task ARetirement_UnpublishesEveryKeyButTheOneSignedWith()
    {
        var running = Open();
        var before = Token(running);
        Open().Rotate();

        Open().Retire();

        running.PublishedKeys.Keys.Select(k => k.KeyId).ShouldBe([running.CurrentKeyId]);
        (await VerifyAgainst(running.PublishedKeys, before)).Refusal.ShouldBe(TokenRefusal.UnknownKey);
        (await VerifyAgainst(running.PublishedKeys, Token(running))).IsVerified.ShouldBeTrue();
    }

    // The file is the private key.
    [SkippableFact]
    public void TheKeyFile_IsReadableByTheIssuersUserAlone()
    {
        Skip.If(OperatingSystem.IsWindows());

        Open();

        File.GetUnixFileMode(Path.Combine(_directory, SigningKeyStore.FileName))
            .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // A test or eval stack signs with the fixed key it hands in, and nothing is generated or kept.
    [Fact]
    public async Task AProvidedKey_IsSignedWithAndNothingIsWritten()
    {
        var keys = SigningKeyStore.Open(new SigningKeySettings
        {
            KeyDirectory = _directory, PrivateKey = TestSigningKeys.Agent
        });

        var token = Token(keys);

        Directory.Exists(_directory).ShouldBeFalse();
        keys.CurrentKeyId.ShouldBe(TestSigningKeys.Store(TestSigningKeys.Agent).CurrentKeyId);
        (await VerifyAgainst(TestSigningKeys.Store(TestSigningKeys.Agent).PublishedKeys, token)).IsVerified.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(keys.Rotate);
        Should.Throw<InvalidOperationException>(keys.Retire);
    }

    [Fact]
    public void AnIssuerWithNeitherADirectoryNorAKey_RefusesToStart()
    {
        Should.Throw<InvalidOperationException>(() => SigningKeyStore.Open(new SigningKeySettings()));
    }
}