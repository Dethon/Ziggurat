using System.ComponentModel;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
    [Description("Create a new conversation for agent-initiated messages")]
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services,
        [Description("Text of the originating prompt; rendered as the user-role bubble")] string? initialPrompt = null,
        [Description("Unused on this channel; voice uses it for satellite targeting")] string? address = null,
        [Description("Existing conversation id: attaches this turn to it instead of creating a topic. With a topic name it is a later target of a fan-out, and a topic is minted over it if none holds it; without one it is the turn-start announce")] string? existingConversationId = null,
        [Description("Who the turn acts for: a topic minted for it is that account's, or the household's when it acts for none")] Principal? principal = null,
        [Description("The conversation is several people's, a group chat: a topic minted for it is the household's whoever's turn it shows")] bool group = false)
    {
        // Whose a minted topic is comes from the turn it shows, decided here: the channel that asked
        // for the conversation to be shown never names where it goes, nor whose it is (ADR 0046).
        var p = new CreateConversationParams
        {
            AgentId = agentId,
            TopicName = topicName,
            Sender = sender,
            InitialPrompt = initialPrompt,
            Principal = principal,
            Group = group
        };

        if (existingConversationId is null)
        {
            // Shared factory generates the conversation identity and persists the topic
            // to Redis (single source of truth shared with the voice channel).
            var creation = await services.GetRequiredService<IConversationFactory>().CreateAsync(p);
            return await OpenAsync(creation, p, services);
        }

        // A later target of a fan-out shows the conversation the first target anchored — a review
        // copy of another channel's conversation is the one case (ADR 0046). The topic is minted over
        // that conversation, so it reads the history the agent writes there, and only the first
        // time: after that the topic holds it, and the turn is announced on it like any other.
        if (!string.IsNullOrEmpty(topicName)
            && await ShowAsync(existingConversationId, p, services) is { } shown)
        {
            return shown;
        }

        return await AttachTurnAsync(existingConversationId, sender, initialPrompt, services);
    }

    private static async Task<string?> ShowAsync(
        string conversationId, CreateConversationParams p, IServiceProvider services)
    {
        // Only a conversation spelled as a topic spells its own has a history a topic can read.
        if (services.GetRequiredService<SessionService>().GetTopicIdByConversationId(conversationId) is not null
            || ConversationIdentity.Parse(conversationId) is not { } identity)
        {
            return null;
        }

        var creation = await services.GetRequiredService<IConversationFactory>().CreateOverAsync(identity, p);
        return creation is null ? null : await OpenAsync(creation, p, services);
    }

    private static async Task<string> OpenAsync(
        ConversationCreation creation, CreateConversationParams p, IServiceProvider services)
    {
        // Register the in-memory session so send_reply/request_approval can resolve it.
        var sessionService = services.GetRequiredService<SessionService>();
        sessionService.StartSession(
            creation.Topic.TopicId, p.AgentId, creation.Identity.ChatId, creation.Identity.ThreadId,
            spaceSlug: "default", topicName: p.TopicName);

        // Notify WebChat clients so the topic appears without refresh — the ones that may see it.
        var hubSender = services.GetRequiredService<IHubNotificationSender>();
        var notification = new TopicChangedNotification(
            TopicChangeType.Created, creation.Topic.TopicId, creation.Topic, SpaceSlug: creation.Topic.SpaceSlug);
        await hubSender.SendToTopicAsync(creation.Topic, creation.Topic.SpaceSlug, "OnTopicChanged", notification);

        // Create a stream so send_reply chunks have somewhere to go. The stream's
        // currentPrompt seeds the user-role bubble on WebChat, so it must be the
        // originating prompt — falling back to topicName for legacy callers.
        var streamService = services.GetRequiredService<StreamService>();
        streamService.GetOrCreateStream(creation.Topic.TopicId, p.InitialPrompt ?? p.TopicName, p.Sender, CancellationToken.None);

        return creation.Identity.ConversationId;
    }

    private static async Task<string> AttachTurnAsync(
        string conversationId,
        string sender,
        string? initialPrompt,
        IServiceProvider services)
    {
        // Turn-start announce for an agent-initiated reply (download alert, schedule
        // result) into an existing conversation. No session means nobody has the topic
        // open on this server — degrade to persisted-only delivery (visible on refresh).
        var sessionService = services.GetRequiredService<SessionService>();
        var topicId = sessionService.GetTopicIdByConversationId(conversationId);
        if (topicId is null || !sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            return conversationId;
        }

        // Mirror the interactive SendMessage idiom: seed the stream with the originating
        // prompt (rendered as the user bubble on resume), buffer the user-role message
        // for already-open browsers, and count this turn toward stream teardown.
        var streamService = services.GetRequiredService<StreamService>();
        streamService.GetOrCreateStream(topicId, initialPrompt ?? string.Empty, sender, CancellationToken.None);
        streamService.TryIncrementPending(topicId);
        if (!string.IsNullOrWhiteSpace(initialPrompt))
        {
            await streamService.WriteMessageAsync(topicId, new ChatStreamMessage
            {
                Content = initialPrompt,
                UserMessage = new UserMessageInfo(sender, DateTimeOffset.UtcNow)
            });
        }

        // Wake viewing clients: their OnStreamStarted handler resumes the stream
        // (buffered replay + live subscription) without any client changes. Only those that may see
        // the topic are woken, since a private one's activity is its owner's business.
        var spaceSlug = session.SpaceSlug ?? "default";
        var topic = await services.GetRequiredService<IThreadStateStore>()
            .GetTopicByChatIdAndThreadIdAsync(session.AgentId, session.ChatId, session.ThreadId);
        await services.GetRequiredService<IHubNotificationSender>().SendToTopicAsync(
            topic, spaceSlug, "OnStreamStarted", new StreamStartedNotification(topicId, spaceSlug));

        return conversationId;
    }
}