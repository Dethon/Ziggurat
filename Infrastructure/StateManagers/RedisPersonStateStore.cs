using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Contracts;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// A person's remembered logins in Redis, one record each under their account's user id, sealed with
// AES-GCM under a key from the deployment's secrets, so a dump of the database hands out nobody's
// accounts. The blob is a version byte, the nonce, the tag and the ciphertext; the user id is the
// associated data, so one account's record copied under another's key does not open. A record
// written under a person's name before accounts is nobody's: the migration re-seals it, and
// nothing here reads one as a fallback.
public sealed class RedisPersonStateStore(IConnectionMultiplexer redis, byte[]? key) : IPersonStateStore
{
    public const int KeyBytes = 32;

    private const string KeyPrefix = "websearch:person:";
    // Version 1 held a list of bare remembered domains rather than logins; it was never deployed,
    // and a record of it is read as nothing remembered rather than as unreadable, so the person
    // logs in again instead of being refused until somebody deletes the key.
    private const byte Version = 2;
    private const byte DomainsVersion = 1;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // The configured key, base64 of 32 bytes; empty is remembering off. Anything else fails
    // startup naming the setting, rather than every save failing one at a time.
    public static byte[]? ParseKey(string? configured, string settingName)
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
                return bytes;
            }
        }
        catch (FormatException)
        {
        }

        throw new InvalidOperationException(
            $"{settingName} must be the base64 of {KeyBytes} random bytes (openssl rand -base64 {KeyBytes}).");
    }

    public bool CanWrite => key is not null;

    public async Task<PersonState?> LoadAsync(string person, CancellationToken ct = default)
    {
        var stored = await redis.GetDatabase().StringGetAsync(KeyPrefix + person);
        if (stored.IsNullOrEmpty)
        {
            return null;
        }

        if (key is null)
        {
            throw new PersonStateUnreadableException(person, "no key is configured to open them (Logins:StateKey)");
        }

        byte[] blob = stored!;
        if (blob.Length > 0 && blob[0] == DomainsVersion)
        {
            return null;
        }

        if (blob.Length < 1 + NonceBytes + TagBytes || blob[0] != Version)
        {
            throw new PersonStateUnreadableException(person, "the stored record is not one this server wrote");
        }

        var nonce = blob.AsSpan(1, NonceBytes);
        var tag = blob.AsSpan(1 + NonceBytes, TagBytes);
        var cipher = blob.AsSpan(1 + NonceBytes + TagBytes);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(person));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new PersonStateUnreadableException(person, "the configured key does not open them (Logins:StateKey)");
        }

        return JsonSerializer.Deserialize<PersonState>(plain, _json);
    }

    public async Task SaveAsync(string person, PersonState state, CancellationToken ct = default)
    {
        if (key is null)
        {
            throw new InvalidOperationException("No key is configured, so nothing can be remembered.");
        }

        var plain = JsonSerializer.SerializeToUtf8Bytes(state, _json);
        var blob = new byte[1 + NonceBytes + TagBytes + plain.Length];
        blob[0] = Version;
        var nonce = blob.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plain, blob.AsSpan(1 + NonceBytes + TagBytes), blob.AsSpan(1 + NonceBytes, TagBytes),
                Encoding.UTF8.GetBytes(person));
        }

        await redis.GetDatabase().StringSetAsync(KeyPrefix + person, blob);
    }
}