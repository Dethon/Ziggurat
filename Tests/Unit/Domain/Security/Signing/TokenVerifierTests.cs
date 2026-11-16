using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.DTOs.Channel;
using Domain.Security.Signing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Security.Signing;

// Signing and verifying on their own: what a verifier believes of a token, and every reason it
// refuses one. A server's gate, the agent and the apps behind the proxy all answer a call by what
// this says, so a token it admits is one its issuer signed, for this audience, and still in date.
public class TokenVerifierTests
{
    private const string Audience = "memory";
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(2);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly SigningKeyStore _agent = TestSigningKeys.Store(TestSigningKeys.Agent);

    private TokenVerifier Verifier => new(TokenIssuers.Agent, _agent, _clock);

    private IdentityClaims PersonClaims(string audience = Audience) =>
        IdentityClaims.Person("0123456789abcdef0123456789abcdef", "home", "signalr", ["member"])
            .Stamped(TokenIssuers.Agent, audience, _clock.GetUtcNow(), _lifetime);

    [Fact]
    public async Task ATokenItsIssuerSigned_VerifiesWithTheClaimsItWasSignedWith()
    {
        var claims = PersonClaims();
        var token = new TokenSigner(_agent).Sign(claims);

        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.Refusal.ShouldBeNull();
        verification.Claims.ShouldNotBeNull();
        verification.Claims.Subject.ShouldBe("0123456789abcdef0123456789abcdef");
        verification.Claims.TenantId.ShouldBe("home");
        verification.Claims.ChannelId.ShouldBe("signalr");
        verification.Claims.Roles.ShouldBe(["member"]);
        verification.Claims.Id.ShouldBe(claims.Id);
        verification.Claims.IsGuest.ShouldBeFalse();
        verification.Claims.Account.ShouldBe(new Account("home", "0123456789abcdef0123456789abcdef"));
    }

    // Thirty seconds of drift between containers is forgiven, and not a second more.
    [Theory]
    [InlineData(29, null)]
    [InlineData(31, TokenRefusal.Expired)]
    public async Task ATokenPastItsExpiry_VerifiesOnlyWithinTheSkew(int secondsPastExpiry, TokenRefusal? refusal)
    {
        var token = new TokenSigner(_agent).Sign(PersonClaims());

        _clock.Advance(_lifetime + TimeSpan.FromSeconds(secondsPastExpiry));
        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.Refusal.ShouldBe(refusal);
    }

    // A call token names the one server it is for; another server presented it is refused.
    [Fact]
    public async Task ATokenAimedAtAnotherAudience_IsRefusedAsWrongAudience()
    {
        var token = new TokenSigner(_agent).Sign(PersonClaims(audience: "sandbox"));

        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.Refusal.ShouldBe(TokenRefusal.WrongAudience);
    }

    // The agent holds both issuers' keys in mind: an assertion is not a call token because it
    // happens to verify, and neither is anything a key it trusts signed under another issuer's name.
    [Fact]
    public async Task ATokenSignedInAnotherIssuersName_IsRefusedAsWrongIssuer()
    {
        var claims = IdentityClaims.Person("0123456789abcdef0123456789abcdef", "home", "signalr", [])
            .Stamped(TokenIssuers.Accounts, Audience, _clock.GetUtcNow(), _lifetime);
        var token = new TokenSigner(_agent).Sign(claims);

        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.Refusal.ShouldBe(TokenRefusal.WrongIssuer);
    }

    // A token minted for later is no more valid now than one that has run out.
    [Theory]
    [InlineData(29, null)]
    [InlineData(31, TokenRefusal.NotYetValid)]
    public async Task ATokenNotValidYet_VerifiesOnlyWithinTheSkew(int secondsEarly, TokenRefusal? refusal)
    {
        var claims = PersonClaims() with { NotBefore = _clock.GetUtcNow() + TimeSpan.FromSeconds(secondsEarly) };
        var token = new TokenSigner(_agent).Sign(claims);

        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.Refusal.ShouldBe(refusal);
    }

    // Somebody who holds a token rewrites who it is for and keeps the issuer's signature.
    [Fact]
    public async Task ATokenWhosePayloadWasRewritten_IsRefusedAsBadSignature()
    {
        var parts = new TokenSigner(_agent).Sign(PersonClaims()).Split('.');
        var forged = PersonClaims() with { Subject = "fedcba9876543210fedcba9876543210" };
        var forgedPayload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(forged));

        var verification = await Verifier.VerifyAsync($"{parts[0]}.{forgedPayload}.{parts[2]}", Audience);

        verification.Refusal.ShouldBe(TokenRefusal.BadSignature);
        verification.Claims.ShouldBeNull();
    }

    [Fact]
    public async Task ATokenWhoseSignatureWasAltered_IsRefusedAsBadSignature()
    {
        var parts = new TokenSigner(_agent).Sign(PersonClaims()).Split('.');
        var signature = Base64Url.DecodeFromChars(parts[2]);
        signature[0] ^= 0x01;

        var verification = await Verifier.VerifyAsync(
            $"{parts[0]}.{parts[1]}.{Base64Url.EncodeToString(signature)}", Audience);

        verification.Refusal.ShouldBe(TokenRefusal.BadSignature);
    }

    // The accounts service's key is not the agent's: a token it signed names a key the agent's set
    // does not hold.
    [Fact]
    public async Task ATokenSignedByAKeyTheIssuerDoesNotPublish_IsRefusedAsUnknownKey()
    {
        var token = new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Accounts)).Sign(PersonClaims());

        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.Refusal.ShouldBe(TokenRefusal.UnknownKey);
    }

    // The same forgery, naming the issuer's own key id in its header.
    [Fact]
    public async Task ATokenSignedByAnotherKeyUnderTheIssuersKeyId_IsRefusedAsBadSignature()
    {
        var foreign = new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Accounts)).Sign(PersonClaims()).Split('.');
        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new { alg = "ES256", kid = _agent.CurrentKeyId, typ = "JWT" }));
        var signature = SignWith(TestSigningKeys.Accounts, $"{header}.{foreign[1]}");

        var verification = await Verifier.VerifyAsync($"{header}.{foreign[1]}.{signature}", Audience);

        verification.Refusal.ShouldBe(TokenRefusal.BadSignature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoTokenAtAll_IsRefusedAsMissing(string? token)
    {
        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.Refusal.ShouldBe(TokenRefusal.Missing);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("!!!.???.###")]
    public async Task SomethingThatIsNotAToken_IsRefusedAsMalformed(string token)
    {
        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.Refusal.ShouldBe(TokenRefusal.Malformed);
    }

    // Signed, but by an issuer that left out who it is for or until when: nothing to act on.
    [Fact]
    public async Task ASignedTokenNamingNobodyOrNoExpiry_IsRefusedAsMalformed()
    {
        var signer = new TokenSigner(_agent);
        var nobody = signer.Sign(PersonClaims() with { Subject = "" });
        var forever = signer.Sign(PersonClaims() with { ExpiresAt = default });

        (await Verifier.VerifyAsync(nobody, Audience)).Refusal.ShouldBe(TokenRefusal.Malformed);
        (await Verifier.VerifyAsync(forever, Audience)).Refusal.ShouldBe(TokenRefusal.Malformed);
    }

    // `alg: none` is the classic way to skip a verifier's check; only ES256 is ever read.
    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    public async Task ATokenClaimingAnotherAlgorithm_IsRefusedAsMalformed(string algorithm)
    {
        var parts = new TokenSigner(_agent).Sign(PersonClaims()).Split('.');
        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new { alg = algorithm, kid = _agent.CurrentKeyId, typ = "JWT" }));

        var verification = await Verifier.VerifyAsync($"{header}.{parts[1]}.{parts[2]}", Audience);

        verification.Refusal.ShouldBe(TokenRefusal.Malformed);
    }

    // The household's guest is a signed claim like any other, and is nobody's account.
    [Fact]
    public async Task AGuestToken_CarriesTheGuestMarkerAndVerifiesLikeAnyOther()
    {
        var claims = IdentityClaims.Guest("home", "voice")
            .Stamped(TokenIssuers.Agent, Audience, _clock.GetUtcNow(), _lifetime);
        var token = new TokenSigner(_agent).Sign(claims);

        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.IsVerified.ShouldBeTrue();
        verification.Claims.IsGuest.ShouldBeTrue();
        verification.Claims.Subject.ShouldBe(IdentityClaims.GuestSubject);
        verification.Claims.TenantId.ShouldBe("home");
        verification.Claims.ChannelId.ShouldBe("voice");
        verification.Claims.Account.ShouldBeNull();
    }

    // A call the agent makes for nobody — a fire nobody stands behind, its own channel plumbing — is
    // signed all the same, as nobody: a marker of its own, never an empty subject, with no household
    // and no account behind it. Only nobody may leave the household out.
    [Fact]
    public async Task ANobodyToken_CarriesTheNobodyMarkerAndNoHousehold()
    {
        var token = new TokenSigner(_agent).Sign(IdentityClaims.Nobody("scheduling")
            .Stamped(TokenIssuers.Agent, Audience, _clock.GetUtcNow(), _lifetime));
        var unhoused = new TokenSigner(_agent).Sign(PersonClaims() with { TenantId = "" });

        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.IsVerified.ShouldBeTrue();
        verification.Claims.IsNobody.ShouldBeTrue();
        verification.Claims.IsGuest.ShouldBeFalse();
        verification.Claims.Subject.ShouldBe(IdentityClaims.NobodySubject);
        verification.Claims.ChannelId.ShouldBe("scheduling");
        verification.Claims.Account.ShouldBeNull();
        (await Verifier.VerifyAsync(unhoused, Audience)).Refusal.ShouldBe(TokenRefusal.Malformed);
    }

    // A refused token is never the guest: there is no identity in a refusal to fall back on.
    [Fact]
    public async Task ARefusedGuestToken_CarriesNoIdentity()
    {
        var claims = IdentityClaims.Guest("home", "voice")
            .Stamped(TokenIssuers.Agent, Audience, _clock.GetUtcNow(), _lifetime);
        var token = new TokenSigner(_agent).Sign(claims);

        _clock.Advance(TimeSpan.FromHours(1));
        var verification = await Verifier.VerifyAsync(token, Audience);

        verification.IsVerified.ShouldBeFalse();
        verification.Claims.ShouldBeNull();
    }

    // A delegation is the identity and a few claims of its own; whatever a derived claim set adds is
    // signed and verified with the rest.
    [Fact]
    public async Task ADerivedClaimSet_VerifiesWithWhatItAdds()
    {
        var claims = (ObjectClaims)new ObjectClaims
        {
            Subject = "0123456789abcdef0123456789abcdef", TenantId = "home", Kind = "schedule", ObjectId = "s-1"
        }.Stamped(TokenIssuers.Agent, Audience, _clock.GetUtcNow(), _lifetime);
        var token = new TokenSigner(_agent).Sign(claims);

        var verification = await Verifier.VerifyAsync<ObjectClaims>(token, Audience);

        verification.IsVerified.ShouldBeTrue();
        verification.Claims.Kind.ShouldBe("schedule");
        verification.Claims.ObjectId.ShouldBe("s-1");
        verification.Claims.Subject.ShouldBe("0123456789abcdef0123456789abcdef");
    }

    private static string SignWith(string privateKey, string signingInput)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        return Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private sealed record ObjectClaims : IdentityClaims
    {
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "";

        [JsonPropertyName("obj")]
        public string ObjectId { get; init; } = "";
    }
}