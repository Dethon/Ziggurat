using System.ComponentModel;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    // Voice keeps no topics: the conversation a delivery speaks into is always somebody else's — the
    // channel that anchors the fan-out, or a topic the agent minted for a fire only voice delivers —
    // so the agent's naming, sender and principal arguments are not declared, and are ignored.
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
    [Description("Attach a voice delivery to a conversation, so its reply is spoken on voice satellites")]
    public static string McpRun(
        IServiceProvider services,
        [Description("Satellite selector: a satellite id, or 'all'/empty for every satellite")] string? address = null,
        [Description("The conversation this voice delivery attaches to: the one another channel or the agent minted")] string? existingConversationId = null)
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

        var conversationId = existingConversationId
            ?? throw new McpException("Voice attaches to a conversation it is handed and mints none; none was named");

        // A live satellite session already owns this conversation: send_reply routes the turn
        // through it, so no binding is needed — and a stray binding's expiry would flush the shared
        // accumulator mid-turn. Without a live session the binding below delivers the turn as an
        // announcement on the addressed satellite(s).
        var manager = services.GetRequiredService<VoiceConversationManager>();
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var liveSatelliteId = manager.ResolveSatelliteId(conversationId);
        if (liveSatelliteId is not null && sessions.Get(liveSatelliteId) is not null)
        {
            return conversationId;
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