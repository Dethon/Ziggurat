namespace Domain.Security.Signing;

// An issuer's signing keys: the one it signs with now and the ones it signed with before that are
// still published, so a token signed a moment before a rotation keeps verifying.
public sealed class SigningKeyStore : IVerificationKeys
{
    private readonly IReadOnlyList<SigningKey> _keys;

    private SigningKeyStore(IReadOnlyList<SigningKey> keys)
    {
        _keys = keys;
    }

    public string CurrentKeyId => Current.Id;

    public JsonWebKeySet PublishedKeys => new([.. _keys.Select(k => k.PublicKey)]);

    internal SigningKey Current => _keys[^1];

    public static SigningKeyStore Open(SigningKeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SigningKeyStore([SigningKey.Import(settings.PrivateKey)]);
    }

    public ValueTask<VerificationKey?> FindAsync(string keyId, CancellationToken ct) =>
        ValueTask.FromResult(_keys.FirstOrDefault(k => k.Id == keyId)?.VerificationKey);
}