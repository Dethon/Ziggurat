using System.Collections.Concurrent;
using McpChannelTelegram.Settings;
using Telegram.Bot;

namespace McpChannelTelegram.Services;

public sealed class BotRegistry
{
    private readonly Dictionary<string, ITelegramBotClient> _botsByAgent;
    private readonly ConcurrentDictionary<long, string> _chatToAgent = new();

    public BotRegistry(IEnumerable<AgentBotConfig> bots)
    {
        _botsByAgent = bots.ToDictionary(b => b.AgentId, b => (ITelegramBotClient)new TelegramBotClient(b.BotToken));
    }

    internal BotRegistry(Dictionary<string, ITelegramBotClient> botsByAgent)
    {
        _botsByAgent = botsByAgent;
    }

    public ITelegramBotClient GetBotForAgent(string agentId) =>
        FindBotForAgent(agentId)
        ?? throw new KeyNotFoundException($"No bot registered for agent '{agentId}'");

    // An attachment reference names its agent, so a fetch resolves the bot from the reference
    // alone with no chat mapping to consult — including from a cold start. A reference for an
    // agent this server does not run is a miss rather than a failure.
    public ITelegramBotClient? FindBotForAgent(string agentId) => _botsByAgent.GetValueOrDefault(agentId);

    public ITelegramBotClient? GetBotForChat(long chatId) =>
        _chatToAgent.TryGetValue(chatId, out var agentId) && _botsByAgent.TryGetValue(agentId, out var client)
            ? client
            : null;

    public IReadOnlyList<(string AgentId, ITelegramBotClient Client)> GetAllBots() =>
        _botsByAgent.Select(kv => (kv.Key, kv.Value)).ToList();

    public void RegisterChatAgent(long chatId, string agentId) =>
        _chatToAgent[chatId] = agentId;
}