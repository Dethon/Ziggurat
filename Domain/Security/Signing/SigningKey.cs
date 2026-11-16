using System.Security.Cryptography;

namespace Domain.Security.Signing;

// One of an issuer's private keys, with the id and public half it is published under.
internal sealed class SigningKey
{
    private readonly ECDsa _key;

    private SigningKey(ECDsa key)
    {
        _key = key;
        PublicKey = JsonWebKey.Of(key);
        VerificationKey = VerificationKey.Of(PublicKey.KeyId, key);
    }

    public string Id => PublicKey.KeyId;

    public JsonWebKey PublicKey { get; }

    public VerificationKey VerificationKey { get; }

    public static SigningKey Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static SigningKey Import(string pkcs8Base64)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8Base64.Trim()), out _);
        var curve = key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
        return curve == ECCurve.NamedCurves.nistP256.Oid.Value
            ? new SigningKey(key)
            : throw new CryptographicException($"a signing key must be on P-256, not on curve {curve}");
    }

    public string Export() => Convert.ToBase64String(_key.ExportPkcs8PrivateKey());

    // JWS's ES256 signature is the raw R||S pair, not the DER sequence some APIs default to.
    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}