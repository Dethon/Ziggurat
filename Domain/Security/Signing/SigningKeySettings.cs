namespace Domain.Security.Signing;

// Where an issuer's signing keys come from. A deployment names a directory in the issuer's own
// volume, where the key is generated on first start and kept; nothing about it is a secret to
// provision, so it is in no `.env`. A test or eval stack hands the issuer a fixed key instead, so the
// suite needs no keys provisioned and signs the same way every run.
public sealed record SigningKeySettings
{
    public string KeyDirectory { get; init; } = "";

    // A P-256 private key, PKCS#8 DER in base64 on one line. When set it is the only key: the
    // directory is never read or written, and the key is neither rotated nor retired here.
    public string PrivateKey { get; init; } = "";
}