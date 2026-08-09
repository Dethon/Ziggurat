using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public static class ChannelProtocol
{
    public const string MessageNotification = "notifications/channel/message";
    public const string CancelNotification = "notifications/channel/cancel";
    public const string SendReplyTool = "send_reply";
    public const string RequestApprovalTool = "request_approval";
    public const string CreateConversationTool = "create_conversation";
    public const string RegisterAgentsTool = "register_agents";
    public const string ReceiveTool = "channel_receive";

    // How the agent reaches a channel's upload store: by naming a reference, never by mounting it.
    // Hidden from the model like every other channel-protocol tool — one upload store serves every
    // conversation, so a visible mount would be a read over everyone else's files (ADR 0021).
    public const string FetchAttachmentTool = "fetch_attachment";

    // How long a channel_receive call may be held open server-side before returning an empty
    // batch. Verified safe: a 45s hold completes on the SDK's default client timeout, and no
    // reverse proxy sits between the agent and a channel server (ChannelEndpoints are
    // container-to-container; Caddy only fronts the browser-facing /hubs/* route).
    public const int DefaultReceiveWaitMs = 30_000;

    // The agent pump's retry backoff ceiling after a failed channel_receive call
    // (McpChannelConnection reads it from here). Part of the liveness contract:
    // ChannelInbox's freshness window is sized from this plus DefaultReceiveWaitMs, so a poll held
    // open in full and then delayed by one worst-case backoff still counts as live.
    public const int MaxReceiveRetryBackoffMs = 30_000;

    // _meta key under which the agent's MCP tool wrapper attaches the current turn's
    // ConversationContext to every tools/call; dual-role servers read it for routing.
    // Vendor-prefixed on purpose: the 2026-07-28 spec reserves any _meta prefix whose second
    // label is "mcp" or "modelcontextprotocol" and asks everyone else for a reverse-DNS label
    // ending in "/". A bare key would share the namespace of progressToken and traceparent,
    // where a later spec revision is free to claim it out from under us.
    public const string ConversationContextMetaKey = "com.herfluffness/conversationContext";

    // Sender attributed to channel/message notifications the system originates on a user's
    // behalf rather than the user themselves — e.g. the /cancel command and download-completion
    // alerts. Keeps these off the initiating user's identity (memory scoping, attribution).
    public const string SystemSender = "system";

    // The agent's channel connections identify themselves as "channel-<channelId>", and derive
    // their ChannelInbox subscriber id from the same string. It is the subscriber id that decides
    // delivery now: every channel_receive poll carries it as an argument, so a client's declared
    // identity no longer selects who receives anything, and dual-role servers no longer filter
    // tool sessions out of the fan-out — there is nothing to filter, one inbox serves the channel.
    public const string ChannelClientNamePrefix = "channel-";

    // A TypeInfoResolver is mandatory: the MCP SDK's SendNotificationAsync calls
    // JsonSerializerOptions.MakeReadOnly() on these options, which throws if no resolver is set.
    // Without it, channel emitters silently failed to deliver channel/message notifications.
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyDictionary<string, object?> ToArguments<T>(T value)
    {
        using var document = JsonSerializer.SerializeToDocument(value, SerializerOptions);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
    }

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(SerializerOptions);
}