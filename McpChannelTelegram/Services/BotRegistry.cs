using System.Collections.Concurrent;
using Domain.DTOs.Household;
using McpChannelTelegram.Settings;
using Telegram.Bot;

namespace McpChannelTelegram.Services;

// The bots this server polls: the deployment's shared bot, given by its settings, and every
// household's own, as the households' configuration last said (Replace). A chat is answered by the bot
// it last wrote through.
public sealed class BotRegistry
{
    private readonly Func<string, ITelegramBotClient> _connect;
    private readonly ILogger<BotRegistry>? _logger;
    private readonly string? _sharedToken;
    private readonly ConcurrentDictionary<long, TelegramBot> _chats = new();
    private readonly Lock _replacing = new();
    private volatile IReadOnlyList<(TelegramBot Bot, string Token)> _households = [];
    private IReadOnlyList<string> _unpolled = [];

    public BotRegistry(BotSettings settings, ILogger<BotRegistry> logger)
        : this(settings.SharedToken, token => new TelegramBotClient(token), logger)
    {
    }

    // An empty shared token is a stack with no shared bot: only the households' own bots answer.
    internal BotRegistry(string? sharedToken, Func<string, ITelegramBotClient> connect, ILogger<BotRegistry>? logger = null)
    {
        _connect = connect;
        _logger = logger;
        _sharedToken = string.IsNullOrEmpty(sharedToken) ? null : sharedToken;
        Shared = _sharedToken is null ? null : TelegramBot.Shared(connect(_sharedToken));
    }

    public TelegramBot? Shared { get; }

    public IReadOnlyList<TelegramBot> All => [.. Shared is null ? [] : new[] { Shared }, .. _households.Select(h => h.Bot)];

    // The households' bots as configured now: one bound to an agent whose token is unchanged is the
    // very bot already polling, any other is new, and one no longer configured is gone. Telegram
    // answers one poller per token, so a token configured twice — or the shared bot's — is polled once,
    // by the first; a token Telegram's client refuses is left out, so one household's mistake keeps no
    // other's bots from starting. What is left out is logged when it changes, not on every read.
    public BotChanges Replace(IReadOnlyList<HouseholdTelegramBot> configured)
    {
        lock (_replacing)
        {
            var current = _households.ToDictionary(h => h.Bot.Key);
            var byToken = configured
                .Where(bot => bot.Token.Length > 0 && bot.Token != _sharedToken)
                .GroupBy(bot => bot.Token, StringComparer.Ordinal)
                .ToList();
            var unpolled = byToken.SelectMany(g => g.Skip(1))
                .Concat(configured.Where(bot => bot.Token == _sharedToken))
                .Select(bot => $"household {bot.HouseholdId}'s bot for {bot.AgentId} has a token another bot already polls with")
                .ToList();
            var wanted = new List<(TelegramBot Bot, string Token)>();
            foreach (var bot in byToken.Select(g => g.First()))
            {
                if (current.TryGetValue(TelegramBot.KeyOf(bot.HouseholdId, bot.AgentId), out var kept) && kept.Token == bot.Token)
                {
                    wanted.Add(kept);
                    continue;
                }

                try
                {
                    wanted.Add((TelegramBot.Of(bot.HouseholdId, bot.AgentId, _connect(bot.Token)), bot.Token));
                }
                catch (ArgumentException)
                {
                    unpolled.Add($"household {bot.HouseholdId}'s bot for {bot.AgentId} has a token Telegram's client refuses");
                }
            }

            if (!unpolled.SequenceEqual(_unpolled))
            {
                unpolled.ForEach(why => _logger?.LogWarning("A Telegram bot is not polled: {Why}", why));
                _unpolled = unpolled;
            }

            var started = wanted.Where(w => !current.TryGetValue(w.Bot.Key, out var kept) || kept.Bot != w.Bot)
                .Select(w => w.Bot).ToList();
            var stopped = current.Values.Where(c => wanted.All(w => w.Bot != c.Bot)).Select(c => c.Bot).ToList();
            _households = wanted;
            foreach (var chat in _chats.Where(chat => stopped.Contains(chat.Value)).Select(chat => chat.Key))
            {
                _chats.TryRemove(chat, out _);
            }

            return new BotChanges(started, stopped);
        }
    }

    // The bot an attachment reference names. A reference minted before bots were keyed names the agent
    // whose bot it came through — the home's jack or jonas, the only bots there were — which is
    // answered while one bot alone is bound to that agent.
    public TelegramBot? ForReference(string key) =>
        key == TelegramBot.SharedKey
            ? Shared
            : key.Contains('.')
                ? _households.Select(h => h.Bot).FirstOrDefault(bot => bot.Key == key)
                : _households.Select(h => h.Bot).Where(bot => bot.AgentId == key).ToList() is [var only]
                    ? only
                    : null;

    public TelegramBot? Of(string householdId, string agentId) =>
        _households.Select(h => h.Bot).FirstOrDefault(bot => bot.HouseholdId == householdId && bot.AgentId == agentId);

    public ITelegramBotClient? GetBotForChat(long chatId) => _chats.TryGetValue(chatId, out var bot) ? bot.Client : null;

    public void RegisterChat(long chatId, TelegramBot bot) => _chats[chatId] = bot;

    // Who speaks to a person unprompted, when any bot may: their household's bot for the agent, then the
    // household's others, then the shared bot. A bot can write first only in a chat the person opened
    // with it, so the rest are there to be tried after; another household's bot is never one.
    public IReadOnlyList<ITelegramBotClient> BotsPreferring(string householdId, string? agentId) =>
        [.. Preferring(householdId, agentId).Select(bot => bot.Client)];

    public IReadOnlyList<TelegramBot> Preferring(string householdId, string? agentId) =>
    [
        .. _households.Select(h => h.Bot)
            .Where(bot => bot.HouseholdId == householdId)
            .OrderBy(bot => bot.AgentId == agentId ? 0 : 1)
            .Concat(Shared is null ? [] : [Shared])
    ];
}

public sealed record BotChanges(IReadOnlyList<TelegramBot> Started, IReadOnlyList<TelegramBot> Stopped);