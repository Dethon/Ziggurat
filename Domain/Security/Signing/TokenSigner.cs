using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Domain.Security.Signing;

// Signs claims as a compact JWS (RFC 7515) with the issuer's current key, named in the header so a
// verifier holding several of the issuer's keys knows which to check it with.
public sealed class TokenSigner(SigningKeyStore keys)
{
    public string Sign<T>(T claims) where T : IdentityClaims
    {
        ArgumentNullException.ThrowIfNull(claims);
        var key = keys.Current;
        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new JwsHeader(JsonWebKey.Es256, key.Id, JwsHeader.Jwt)));
        // The runtime type, so whatever a derived claim set adds is signed with the rest.
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(claims, claims.GetType()));
        var signingInput = $"{header}.{payload}";
        return $"{signingInput}.{Base64Url.EncodeToString(key.Sign(Encoding.ASCII.GetBytes(signingInput)))}";
    }
}