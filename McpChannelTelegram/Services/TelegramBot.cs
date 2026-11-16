using Telegram.Bot;

namespace McpChannelTelegram.Services;

// One bot this server polls: the deployment's shared bot, which serves every household and lets each
// chat pick which of its household's agents it talks to, or a household's own, bound to one of that
// household's agents and serving its people alone.
//
// The key names the bot in what outlives a poll — an attachment reference, whose file handle only
// this bot can fetch — so it is the same across restarts: the shared bot's is "shared", a household
// bot's its household and agent joined by a dot, which neither a household id nor an agent id holds.
public sealed record TelegramBot(string Key, string? HouseholdId, string? AgentId, ITelegramBotClient Client)
{
    public const string SharedKey = "shared";

    public bool IsShared => HouseholdId is null;

    public static TelegramBot Shared(ITelegramBotClient client) => new(SharedKey, null, null, client);

    public static TelegramBot Of(string householdId, string agentId, ITelegramBotClient client) =>
        new(KeyOf(householdId, agentId), householdId, agentId, client);

    public static string KeyOf(string householdId, string agentId) => $"{householdId}.{agentId}";
}