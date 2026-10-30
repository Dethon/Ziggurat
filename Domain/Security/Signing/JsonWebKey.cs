using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.Security.Signing;

// One public key as an issuer publishes it (RFC 7517): an ES256 key on P-256, named by its RFC 7638
// thumbprint, so the id is a function of the key and nothing has to remember which id goes with it.
public sealed record JsonWebKey(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("crv")] string Curve,
    [property: JsonPropertyName("x")] string X,
    [property: JsonPropertyName("y")] string Y,
    [property: JsonPropertyName("kid")] string KeyId,
    [property: JsonPropertyName("alg")] string Algorithm,
    [property: JsonPropertyName("use")] string Use)
{
    public const string EllipticCurve = "EC";
    public const string P256 = "P-256";
    public const string Es256 = "ES256";
    public const string Signing = "sig";

    internal static JsonWebKey Of(ECDsa key)
    {
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        var x = Base64Url.EncodeToString(point.X);
        var y = Base64Url.EncodeToString(point.Y);
        return new JsonWebKey(EllipticCurve, P256, x, y, Thumbprint(x, y), Es256, Signing);
    }

    // RFC 7638: SHA-256 over the required members, lexically ordered, with no whitespace.
    private static string Thumbprint(string x, string y) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $$"""{"crv":"{{P256}}","kty":"{{EllipticCurve}}","x":"{{x}}","y":"{{y}}"}""")));
}

// What an issuer serves for its verifiers to fetch: its current key and every one not yet retired.
public sealed record JsonWebKeySet([property: JsonPropertyName("keys")] IReadOnlyList<JsonWebKey> Keys)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static JsonWebKeySet Parse(string json) =>
        JsonSerializer.Deserialize<JsonWebKeySet>(json) ?? throw new JsonException("a JWKS document was null");
}