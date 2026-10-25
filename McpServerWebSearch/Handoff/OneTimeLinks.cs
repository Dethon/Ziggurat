using System.Security.Cryptography;
using System.Text.Json;
using StackExchange.Redis;

namespace McpServerWebSearch.Handoff;

// The one-time links this server hands out: a handoff's and a logins page's. A token names what
// it opens and is spent by the first claim, atomically, so a forwarded or leaked link is worth
// nothing once used; Redis forgets it at its expiry either way.
public sealed class OneTimeLinks(IConnectionMultiplexer redis)
{
    private const string KeyPrefix = "websearch:link:";

    public sealed record Ticket(string Kind, string Id);

    public async Task<string> MintAsync(Ticket ticket, TimeSpan lifetime)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await redis.GetDatabase().StringSetAsync(KeyPrefix + token, JsonSerializer.Serialize(ticket), lifetime);
        return token;
    }

    // Answers without spending, so a link previewer or a prefetch can fetch the page and the
    // person's own tap still works.
    public Task<bool> IsLiveAsync(string token) => redis.GetDatabase().KeyExistsAsync(KeyPrefix + token);

    public async Task<Ticket?> ClaimAsync(string token, string kind)
    {
        var value = await redis.GetDatabase().StringGetDeleteAsync(KeyPrefix + token);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var ticket = JsonSerializer.Deserialize<Ticket>(value.ToString());
        return ticket?.Kind == kind ? ticket : null;
    }

    public Task RevokeAsync(string token) => redis.GetDatabase().KeyDeleteAsync(KeyPrefix + token);
}