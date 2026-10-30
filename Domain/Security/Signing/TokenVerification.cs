using System.Diagnostics.CodeAnalysis;

namespace Domain.Security.Signing;

// Why a token was not believed. A gate reports it, so each names a different mistake to go and
// look for: a caller that sent nothing, an issuer the verifier cannot reach, a clock or a lifetime,
// a token aimed elsewhere, or one somebody other than the issuer made.
public enum TokenRefusal
{
    Missing,
    Malformed,
    UnknownKey,
    KeysUnavailable,
    BadSignature,
    WrongIssuer,
    WrongAudience,
    Expired,
    NotYetValid
}

// Either the claims a token verified with, or why it did not. Never both, never neither: a token
// that failed carries no claims for anything to fall back on.
public sealed record TokenVerification<T> where T : IdentityClaims
{
    private TokenVerification(T? claims, TokenRefusal? refusal)
    {
        Claims = claims;
        Refusal = refusal;
    }

    public T? Claims { get; }

    public TokenRefusal? Refusal { get; }

    [MemberNotNullWhen(true, nameof(Claims))]
    [MemberNotNullWhen(false, nameof(Refusal))]
    public bool IsVerified => Claims is not null;

    public static TokenVerification<T> Verified(T claims) => new(claims, null);

    public static TokenVerification<T> Refused(TokenRefusal reason) => new(null, reason);
}