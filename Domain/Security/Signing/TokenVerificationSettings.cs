namespace Domain.Security.Signing;

// How a verifier treats time: the drift between containers it forgives, and how long it serves a
// fetched JWKS before asking for it again. The cache lifetime is also how long a retired key may
// still be believed somewhere, which is what an operator waits out between a rotation and the
// retirement of the key it replaced.
public sealed record TokenVerificationSettings
{
    // Forgiven on every expiry and not-before, so a few seconds of drift never refuses a call.
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeysLifetime { get; init; } = TimeSpan.FromMinutes(10);

    // The least time between two fetches a token naming an unknown key can cause: short enough that a
    // freshly rotated key is picked up within it, long enough that a stream of made-up ids cannot
    // turn into a stream of requests to the issuer.
    public TimeSpan KeysRefetchInterval { get; init; } = TimeSpan.FromSeconds(10);
}