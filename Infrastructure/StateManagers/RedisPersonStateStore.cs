using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Domain.Security.Sealing;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// A person's remembered logins in Redis, one record each under their account's user id, sealed under
// a key from the deployment's secrets, so a dump of the database hands out nobody's accounts. The
// user id is the associated data, so one account's record copied under another's key does not open.
// A record written under a person's name before accounts is nobody's: the migration re-seals it, and
// nothing here reads one as a fallback.
public sealed class RedisPersonStateStore(IConnectionMultiplexer redis, SealingKey? key) : IPersonStateStore
{
    private const string KeyPrefix = "websearch:person:";
    // Version 1 held a list of bare remembered domains rather than logins; it was never deployed,
    // and a record of it is read as nothing remembered rather than as unreadable, so the person
    // logs in again instead of being refused until somebody deletes the key.
    private const byte DomainsVersion = 1;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

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
        if (blob[0] == DomainsVersion)
        {
            return null;
        }

        var opened = key.Open(blob, Encoding.UTF8.GetBytes(person));
        return opened.IsOpened
            ? JsonSerializer.Deserialize<PersonState>(opened.Value, _json)
            : throw new PersonStateUnreadableException(person, opened.Refusal switch
            {
                SealRefusal.DoesNotOpen => "the configured key does not open them (Logins:StateKey)",
                _ => "the stored record is not one this server wrote"
            });
    }

    public async Task SaveAsync(string person, PersonState state, CancellationToken ct = default)
    {
        if (key is null)
        {
            throw new InvalidOperationException("No key is configured, so nothing can be remembered.");
        }

        var blob = key.Seal(JsonSerializer.SerializeToUtf8Bytes(state, _json), Encoding.UTF8.GetBytes(person));
        await redis.GetDatabase().StringSetAsync(KeyPrefix + person, blob);
    }
}