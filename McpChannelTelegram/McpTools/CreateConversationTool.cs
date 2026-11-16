using System.ComponentModel;
using Domain.Conversations;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.McpTools;

// A turn nobody started here — a schedule's fire whose author asked for it on Telegram — is delivered
// to its person's private chat with the bot, whose id is their own Telegram id, so the reply lands
// where they talk: the conversation is that chat's. By the first bot that can write there, in the
// order BotRegistry.Preferring gives (their household's bot for the agent, its others, the shared
// bot), asked with a "typing" that the person sees while the fire runs: a bot can write first only to
// someone who opened a chat with it. Who it is for is the call's principal, as an offered approval
// names its asker; nobody, or somebody with no Telegram or no bot that can write to them, opens
// nothing, and the fan-out skips the target. A later turn's announce on a conversation this channel
// already holds has nothing to set up.
[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
    [Description("Open the person's Telegram private chat for a turn they did not start there")]
    public static Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name; empty for a turn-start announce")] string topicName,
        [Description("Who the turn is labelled as")] string sender,
        IServiceProvider services,
        [Description("Text of the originating prompt")] string? initialPrompt = null,
        [Description("Unused on this channel")] string? address = null,
        [Description("An existing conversation id: with no topic name, the announce of a later turn in it")] string? existingConversationId = null,
        [Description("Who the turn acts for: its private chat is the one opened")] Principal? principal = null,
        [Description("Unused on this channel: a group's conversation is the group's own")] bool group = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(agentId, topicName, existingConversationId, principal, services, cancellationToken);

    public static async Task<string> RunAsync(
        string agentId, string topicName, string? existingConversationId, Principal? principal,
        IServiceProvider services, CancellationToken ct)
    {
        if (existingConversationId is not null && string.IsNullOrEmpty(topicName)
            && ConversationIdentity.Parse(existingConversationId) is not null)
        {
            return existingConversationId;
        }

        if (principal?.Account is not { } account)
        {
            throw new McpException("The turn acts for nobody, so there is no Telegram chat to deliver it to.");
        }

        var access = services.GetRequiredService<TelegramAccess>();
        if (await access.PrivateChatOfAsync(account, ct) is not { } chatId)
        {
            throw new McpException("The person has no Telegram linked, so the turn cannot be delivered there.");
        }

        var registry = services.GetRequiredService<BotRegistry>();
        var bots = registry.Preferring(account.TenantId, agentId);
        TelegramBot? writer = null;
        if (!await PrivateChatOffer.ShowAsync([.. bots.Select(bot => bot.Client)], async client =>
            {
                await client.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                writer = bots.First(bot => bot.Client == client);
                return true;
            }) || writer is null)
        {
            throw new McpException("No bot can write to the person's Telegram: they never opened a chat with one.");
        }

        registry.RegisterChat(chatId, writer);
        return ConversationIdentity.ForChat(chatId, null).ConversationId;
    }
}