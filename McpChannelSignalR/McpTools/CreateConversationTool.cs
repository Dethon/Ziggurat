using System.ComponentModel;
using Domain.Contracts;
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
        [Description("Existing conversation id: attaches this turn to it (turn-start announce) instead of creating a topic")] string? existingConversationId = null)
    {
        if (existingConversationId is not null)
        {
            return await AttachTurnAsync(existingConversationId, sender, initialPrompt, services);
        }

        var p = new CreateConversationParams
        {
            AgentId = agentId,
            TopicName = topicName,
            Sender = sender,
            InitialPrompt = initialPrompt
        };

        // Shared factory generates the conversation identity and persists the topic
        // to Redis (single source of truth shared with the voice channel).
        var factory = services.GetRequiredService<IConversationFactory>();
        var creation = await factory.CreateAsync(p);

        // Register the in-memory session so send_reply/request_approval can resolve it.
        var sessionService = services.GetRequiredService<SessionService>();
        sessionService.StartSession(
            creation.Topic.TopicId, agentId, creation.Identity.ChatId, creation.Identity.ThreadId,
            spaceSlug: "default", topicName: topicName);

        // Notify WebChat clients so the topic appears without refresh.
        var hubSender = services.GetRequiredService<IHubNotificationSender>();
        var notification = new TopicChangedNotification(
            TopicChangeType.Created, creation.Topic.TopicId, creation.Topic, SpaceSlug: "default");
        await hubSender.SendToGroupAsync("space:default", "OnTopicChanged", notification);

        // Create a stream so send_reply chunks have somewhere to go. The stream's
        // currentPrompt seeds the user-role bubble on WebChat, so it must be the
        // originating prompt — falling back to topicName for legacy callers.
        var streamService = services.GetRequiredService<StreamService>();
        streamService.GetOrCreateStream(creation.Topic.TopicId, initialPrompt ?? topicName, sender, CancellationToken.None);

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
        // (buffered replay + live subscription) without any client changes.
        var spaceSlug = session.SpaceSlug ?? "default";
        var hubSender = services.GetRequiredService<IHubNotificationSender>();
        await hubSender.SendToGroupAsync(
            $"space:{spaceSlug}",
            "OnStreamStarted",
            new StreamStartedNotification(topicId, spaceSlug));

        return conversationId;
    }
}