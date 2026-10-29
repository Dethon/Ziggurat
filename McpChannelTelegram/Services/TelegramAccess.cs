using System.Collections.Concurrent;
using System.Globalization;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpChannelTelegram.Settings;

namespace McpChannelTelegram.Services;

// Who a Telegram id is and which household a group is bound to, as the accounts service last said,
// kept for as long as LinkCacheSettings trusts each answer. An outage is never kept: the resolver
// throws, and nothing is remembered for the id it was asked about.
public sealed class TelegramAccess(
    IAccountResolver resolver, ITelegramAccounts accounts, LinkCacheSettings cache, TimeProvider time)
{
    public const string Channel = "telegram";

    private readonly ConcurrentDictionary<long, Answer<ResolvedAccount>> _people = new();
    private readonly ConcurrentDictionary<long, Answer<string>> _groups = new();

    public Task<ResolvedAccount?> PersonAsync(long telegramId, CancellationToken ct) =>
        CachedAsync(_people, telegramId,
            () => resolver.ResolveAsync(Channel, telegramId.ToString(CultureInfo.InvariantCulture), ct));

    public Task<string?> GroupTenantAsync(long chatId, CancellationToken ct) =>
        CachedAsync(_groups, chatId, () => accounts.GroupTenantAsync(chatId, ct));

    // A private chat's id is its person's own Telegram id, and a group's is negative, so the chat
    // alone says whose household it is: the person's, or the one the group is bound to.
    public async Task<string?> ChatTenantAsync(long chatId, CancellationToken ct) =>
        chatId > 0
            ? (await PersonAsync(chatId, ct))?.Account.TenantId
            : await GroupTenantAsync(chatId, ct);

    // What a redemption or a bind just made true, so the person or the group is let in on their very
    // next message, whatever refusal was remembered a moment before.
    public void Linked(long telegramId, ResolvedAccount person) =>
        _people[telegramId] = new Answer<ResolvedAccount>(person, time.GetUtcNow() + cache.Linked);

    public void Bound(long chatId, string tenantId) =>
        _groups[chatId] = new Answer<string>(tenantId, time.GetUtcNow() + cache.Linked);

    private async Task<T?> CachedAsync<T>(ConcurrentDictionary<long, Answer<T>> answers, long key, Func<Task<T?>> ask)
        where T : class
    {
        if (answers.TryGetValue(key, out var kept) && kept.Until > time.GetUtcNow())
        {
            return kept.Value;
        }

        var value = await ask();
        answers[key] = new Answer<T>(value, time.GetUtcNow() + (value is null ? cache.Unlinked : cache.Linked));
        return value;
    }

    private sealed record Answer<T>(T? Value, DateTimeOffset Until) where T : class;
}