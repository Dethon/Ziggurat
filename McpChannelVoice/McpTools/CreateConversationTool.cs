using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
    [Description("Create a conversation that speaks a scheduled reply on voice satellites")]
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services,
        [Description("Text of the originating prompt")] string? initialPrompt = null,
        [Description("Satellite selector: a satellite id, or 'all'/empty for every satellite")] string? address = null,
        [Description("Existing conversation id to attach this voice delivery to, if the primary channel already minted one")] string? existingConversationId = null,
        [Description("Who the turn acts for: a topic minted for it is that account's, or the household's when it acts for none")] Principal? principal = null)
    {
        var registry = services.GetRequiredService<SatelliteRegistry>();
        var delivery = services.GetRequiredService<VoiceDeliveryRegistry>();

        var target = ParseTarget(address);
        IEnumerable<string> satelliteIds =
            target.SatelliteIds ?? (target.SatelliteId is { } single ? [single] : []);
        var unknown = satelliteIds.FirstOrDefault(id => registry.GetById(id) is null);
        if (unknown is not null)
        {
            throw new McpException($"Unknown voice satellite '{unknown}'");
        }

        // When the primary (WebChat) channel already minted the conversation, attach to that
        // same id so the delivery shows as one shared thread (displayed there, spoken here)
        // rather than a duplicate, empty "Scheduled task" thread. When voice is the only
        // delivery, mint a real topic via the shared factory so the schedule still appears in
        // WebChat under the agent — consistent with interactive voice conversations.
        string conversationId;
        if (existingConversationId is not null)
        {
            conversationId = existingConversationId;

            // A live satellite session already owns this conversation: send_reply routes the
            // turn through it, so no binding is needed — and a stray binding's expiry would
            // flush the shared accumulator mid-turn. Without a live session the binding below
            // delivers the turn as an announcement on the addressed satellite(s).
            var manager = services.GetRequiredService<VoiceConversationManager>();
            var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
            var liveSatelliteId = manager.ResolveSatelliteId(conversationId);
            if (liveSatelliteId is not null && sessions.Get(liveSatelliteId) is not null)
            {
                return conversationId;
            }
        }
        else
        {
            var factory = services.GetRequiredService<IConversationFactory>();
            var creation = await factory.CreateAsync(new CreateConversationParams
            {
                AgentId = agentId,
                TopicName = topicName,
                Sender = sender,
                InitialPrompt = initialPrompt,
                Principal = principal
            });
            conversationId = creation.Identity.ConversationId;
        }

        delivery.Bind(conversationId, target);
        return conversationId;
    }

    private static AnnounceTarget ParseTarget(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new AnnounceTarget { All = true };
        }

        var ids = address.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Any(id => id.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return new AnnounceTarget { All = true };
        }

        return ids.Length == 1
            ? new AnnounceTarget { SatelliteId = ids[0] }
            : new AnnounceTarget { SatelliteIds = ids };
    }
}