using System.Collections.Concurrent;
using System.Globalization;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpChannelTelegram.Settings;

namespace McpChannelTelegram.Services;

// Who a Telegram id is and which household a group is bound to, as the accounts service last said,
// kept for as long as LinkCacheSettings trusts each answer. An outage is never kept: the resolver
// throws, and nothing is remembered for the id it was asked about. A person's answer carries the
// service's assertion of them, which the bot puts on their turns while it keeps the answer; it lives
// ten minutes from signing, so the cache keeps an answer for at most half that (conformance-checked)
// and the agent still believes it on arrival.
public sealed class TelegramAccess(
    IAccountResolver resolver, ITelegramAccounts accounts, LinkCacheSettings cache, TimeProvider time)
{
    private readonly ConcurrentDictionary<long, Answer<ResolvedAccount>> _people = new();
    private readonly ConcurrentDictionary<long, Answer<string>> _groups = new();
    private long _version;

    public Task<ResolvedAccount?> PersonAsync(long telegramId, CancellationToken ct) =>
        CachedAsync(_people, telegramId,
            () => resolver.ResolveAsync(ChannelIds.Telegram, telegramId.ToString(CultureInfo.InvariantCulture), ct));

    public Task<string?> GroupTenantAsync(long chatId, CancellationToken ct) =>
        CachedAsync(_groups, chatId, () => accounts.GroupTenantAsync(chatId, ct));

    // A private chat's id is its person's own Telegram id, and a group's is negative, so the chat
    // alone says whose household it is: the person's, or the one the group is bound to.
    public async Task<string?> ChatTenantAsync(long chatId, CancellationToken ct) =>
        chatId > 0
            ? (await PersonAsync(chatId, ct))?.Account.TenantId
            : await GroupTenantAsync(chatId, ct);

    // A person's private chat with the bot, whose id is their own Telegram id: where a question their
    // turn asked is offered. Not kept, since it is asked once per question and never for a stranger.
    public Task<long?> PrivateChatOfAsync(Account account, CancellationToken ct) =>
        accounts.TelegramIdOfAsync(account, ct);

    // A question acting for an account is that account's alone: a tap counts when the Telegram it came
    // from is linked to it, wherever the question is showing.
    public ApprovalAnswerer AnswersFor(Account asker) =>
        async (tapper, ct) => (await PersonAsync(tapper, ct))?.Account == asker;

    // What a redemption or a bind just made true, so the person or the group is let in on their very
    // next message, whatever refusal was remembered a moment before.
    public void Linked(long telegramId, ResolvedAccount person) =>
        _people[telegramId] = new Answer<ResolvedAccount>(person, time.GetUtcNow() + cache.Linked, NextVersion());

    public void Bound(long chatId, string tenantId) =>
        _groups[chatId] = new Answer<string>(tenantId, time.GetUtcNow() + cache.Linked, NextVersion());

    // Each bot polls on its own, so one may seed an answer while another's question about the same id
    // is in flight; the answer asked for first must not then overwrite the newer one.
    private async Task<T?> CachedAsync<T>(ConcurrentDictionary<long, Answer<T>> answers, long key, Func<Task<T?>> ask)
        where T : class
    {
        if (answers.TryGetValue(key, out var kept) && kept.Until > time.GetUtcNow())
        {
            return kept.Value;
        }

        var version = NextVersion();
        var value = await ask();
        var fresh = new Answer<T>(value, time.GetUtcNow() + (value is null ? cache.Unlinked : cache.Linked), version);
        return answers.AddOrUpdate(key, fresh, (_, existing) => existing.Version > version ? existing : fresh).Value;
    }

    private long NextVersion() => Interlocked.Increment(ref _version);

    private sealed record Answer<T>(T? Value, DateTimeOffset Until, long Version) where T : class;
}