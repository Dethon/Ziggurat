using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;

namespace McpChannelTelegram.Services;

// Which of its household's agents a chat on the shared bot talks to. Nobody has to pick: a chat with
// no choice names no agent, and the agent answers with the household's default. /agent lists the
// household's agents — read from its configuration on the assertion of whoever asks, so a person sees
// only their own household's — and /agent <id> switches the chat to one, for everybody in it, until
// somebody switches again. A household's own bot is bound to its agent and switches nothing.
public sealed class ChatAgents(IChatAgentChoices choices, IHouseholdConfiguration households, ILogger<ChatAgents> logger)
{
    public const string Command = "agent";

    // The agent the chat picked, or none. A store that cannot answer just now is a chat that picked
    // none: the household's default answering beats the message going nowhere.
    public async Task<string?> ChosenAsync(string householdId, long chatId, CancellationToken ct)
    {
        try
        {
            return await choices.GetAsync(householdId, chatId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read which agent Telegram chat {ChatId} picked; its household's default answers", chatId);
            return null;
        }
    }

    // The answer to /agent, with or without an agent's id after it.
    public async Task<string> AnswerAsync(TelegramBot bot, long chatId, ResolvedAccount person, string argument, CancellationToken ct)
    {
        if (!bot.IsShared)
        {
            return $"This bot is {bot.AgentId}'s: everything sent here goes to it. "
                   + "The shared bot switches between your household's agents with /agent.";
        }

        var household = person.Account.TenantId;
        HouseholdAgentNames agents;
        try
        {
            agents = await households.AgentNamesAsync(household, HouseholdProof.OfAssertion(person.Assertion), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException or TaskCanceledException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the agents of household {Household} for Telegram chat {ChatId}", household, chatId);
            return "I can't read your household's agents right now. Try again in a moment.";
        }

        if (argument.Length == 0)
        {
            return List(agents, await ChosenAsync(household, chatId, ct) ?? agents.DefaultAgent);
        }

        if (agents.Agents.FirstOrDefault(a => a.Id.Equals(argument, StringComparison.OrdinalIgnoreCase)) is not { } picked)
        {
            return $"Your household has no agent called '{argument}'.\n" + List(agents, await ChosenAsync(household, chatId, ct) ?? agents.DefaultAgent);
        }

        try
        {
            await choices.SetAsync(household, chatId, picked.Id, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not keep Telegram chat {ChatId}'s choice of {AgentId}", chatId, picked.Id);
            return "I couldn't switch just now. Try again in a moment.";
        }

        return $"{picked.Name} answers here now.";
    }

    private static string List(HouseholdAgentNames agents, string? answering) =>
        string.Join("\n",
        [
            "Your household's agents:",
            .. agents.Agents.Select(a => $"• {a.Name} — /agent {a.Id}{(a.Id == answering ? " (answering here)" : "")}"),
            "Send /agent and an agent's id to switch who answers in this chat."
        ]);
}