using System.Security.Cryptography;

namespace Domain.Security.Signing;

// Where a verifier finds the public key a token names: an issuer's JWKS fetched over the network
// (JwksCache), or the agent's own published keys when it checks a delegation it signed itself
// (SigningKeyStore).
public interface IVerificationKeys
{
    // The key published under this id, or null when the issuer publishes none by it. Throws
    // SigningKeysUnavailableException when the issuer's keys could not be had at all, which a gate
    // reports differently from a key the issuer does not hold.
    ValueTask<VerificationKey?> FindAsync(string keyId, CancellationToken ct);
}

public sealed class SigningKeysUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

// A published key ready to check signatures with, built once when its set is read rather than per
// token.
public sealed class VerificationKey
{
    private const int CoordinateLength = 32;

    private readonly ECDsa _key;

    private VerificationKey(string id, ECDsa key)
    {
        Id = id;
        _key = key;
    }

    public string Id { get; }

    // A key this verifier can use, or null for anything else a JWKS may list beside it.
    public static VerificationKey? From(JsonWebKey jwk) =>
        jwk is { KeyType: JsonWebKey.EllipticCurve, Curve: JsonWebKey.P256, KeyId.Length: > 0, X: not null, Y: not null }
        && jwk.Algorithm is null or JsonWebKey.Es256
        && Coordinate(jwk.X) is { } x
        && Coordinate(jwk.Y) is { } y
            ? Create(jwk.KeyId, x, y)
            : null;

    internal static VerificationKey Of(string id, ECDsa key) => new(id, key);

    internal bool Verifies(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        _key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static VerificationKey? Create(string id, byte[] x, byte[] y)
    {
        try
        {
            return new VerificationKey(id, ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y }
            }));
        }
        catch (CryptographicException)
        {
            // Coordinates that are not a point on the curve.
            return null;
        }
    }

    private static byte[]? Coordinate(string encoded) =>
        JwsHeader.Decode(encoded) is { Length: CoordinateLength } bytes ? bytes : null;
}