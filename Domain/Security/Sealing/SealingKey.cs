using System.Security.Cryptography;

namespace Domain.Security.Sealing;

// A 256-bit AES-GCM key that seals a value for one place — the associated data names it: a person's
// user id, a household and field — so a sealed blob copied anywhere else does not open. The blob is
// a version byte, the nonce, the tag and the ciphertext. Its first version is 2 because remembered
// logins wrote exactly this layout before sealing moved here, and their records, copied as they are
// into a tenant kit, must keep opening; 1 was the logins store's own unsealed first format, which
// that store still recognises before asking anything of this.
public sealed class SealingKey
{
    public const int KeyBytes = 32;

    private const byte Version = 2;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HeaderBytes = 1 + NonceBytes + TagBytes;

    private readonly byte[] _key;

    private SealingKey(byte[] key) => _key = key;

    public static SealingKey Generate() => new(RandomNumberGenerator.GetBytes(KeyBytes));

    public static SealingKey Import(ReadOnlySpan<byte> key) =>
        key.Length == KeyBytes
            ? new SealingKey(key.ToArray())
            : throw new CryptographicException($"a sealing key is {KeyBytes} bytes, not {key.Length}");

    // A configured key is the base64 of its bytes; empty is no key, which each caller reads as its
    // feature off. Anything else fails naming the setting, so a typo stops startup rather than every
    // seal failing one at a time.
    public static SealingKey? Parse(string? configured, string settingName)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(configured.Trim());
            if (bytes.Length == KeyBytes)
            {
                return new SealingKey(bytes);
            }
        }
        catch (FormatException)
        {
        }

        throw new InvalidOperationException(
            $"{settingName} must be the base64 of {KeyBytes} random bytes (openssl rand -base64 {KeyBytes}).");
    }

    public byte[] Export() => _key.ToArray();

    public byte[] Seal(ReadOnlySpan<byte> value, ReadOnlySpan<byte> associatedData)
    {
        var blob = new byte[HeaderBytes + value.Length];
        blob[0] = Version;
        var nonce = blob.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, value, blob.AsSpan(HeaderBytes), blob.AsSpan(1 + NonceBytes, TagBytes), associatedData);
        return blob;
    }

    public Unsealing Open(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> associatedData)
    {
        if (blob.Length < HeaderBytes || blob[0] != Version)
        {
            return Unsealing.Refused(SealRefusal.NotSealed);
        }

        var value = new byte[blob.Length - HeaderBytes];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(blob.Slice(1, NonceBytes), blob[HeaderBytes..], blob.Slice(1 + NonceBytes, TagBytes), value,
                associatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            return Unsealing.Refused(SealRefusal.DoesNotOpen);
        }

        return Unsealing.Opened(value);
    }
}