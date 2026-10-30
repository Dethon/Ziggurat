using System.Text;
using System.Text.Json;

namespace Domain.Security.Signing;

// Believes a token only when the issuer it trusts signed it, for the audience asking, and it is in
// date. The signature is checked before anything the payload says, so a forged token is reported as
// forged rather than as whatever its made-up claims would make it.
public sealed class TokenVerifier(
    string issuer,
    IVerificationKeys keys,
    TimeProvider clock,
    TokenVerificationSettings? settings = null)
{
    private readonly TimeSpan _skew = (settings ?? new TokenVerificationSettings()).ClockSkew;

    public Task<TokenVerification<IdentityClaims>> VerifyAsync(
        string? token, string audience, CancellationToken ct = default) =>
        VerifyAsync<IdentityClaims>(token, audience, ct);

    public async Task<TokenVerification<T>> VerifyAsync<T>(
        string? token, string audience, CancellationToken ct = default) where T : IdentityClaims
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TokenVerification<T>.Refused(TokenRefusal.Missing);
        }

        if (token.Split('.') is not [var header, var payload, var signature]
            || JwsHeader.Decode(header) is not { } headerBytes
            || JwsHeader.Decode(payload) is not { } payloadBytes
            || JwsHeader.Decode(signature) is not { } signatureBytes
            || Read<JwsHeader>(headerBytes) is not { Algorithm: JsonWebKey.Es256, KeyId: { Length: > 0 } keyId })
        {
            return TokenVerification<T>.Refused(TokenRefusal.Malformed);
        }

        var key = await keys.FindAsync(keyId, ct);
        if (key is null)
        {
            return TokenVerification<T>.Refused(TokenRefusal.UnknownKey);
        }

        if (!key.Verifies(Encoding.ASCII.GetBytes($"{header}.{payload}"), signatureBytes))
        {
            return TokenVerification<T>.Refused(TokenRefusal.BadSignature);
        }

        var claims = Read<T>(payloadBytes);
        var now = clock.GetUtcNow();
        TokenRefusal? refusal = claims switch
        {
            null or { Subject.Length: 0 } or { TenantId.Length: 0 } or { Id.Length: 0 } => TokenRefusal.Malformed,
            _ when claims.ExpiresAt == default => TokenRefusal.Malformed,
            _ when claims.Issuer != issuer => TokenRefusal.WrongIssuer,
            _ when claims.Audience != audience => TokenRefusal.WrongAudience,
            _ when now >= claims.ExpiresAt + _skew => TokenRefusal.Expired,
            _ when now < claims.NotBefore - _skew => TokenRefusal.NotYetValid,
            _ => null
        };
        return refusal is { } reason ? TokenVerification<T>.Refused(reason) : TokenVerification<T>.Verified(claims!);
    }

    private static TValue? Read<TValue>(byte[] json) where TValue : class
    {
        try
        {
            return JsonSerializer.Deserialize<TValue>(json);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // Not JSON, or a claim of the wrong kind (a date that is not a number).
            return null;
        }
    }
}