using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.FileSystem;
using JetBrains.Annotations;

namespace Agent.Settings;

public record AgentSettings
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required RedisConfiguration Redis { get; init; }
    public required AgentDefinition[] Agents { get; [UsedImplicitly] init; }

    // Who answers when the channel that carried a message named no agent. Empty is legal and means
    // such a message is refused rather than routed by position in the array above.
    public AgentDefaults AgentDefaults { get; [UsedImplicitly] init; } = new();
    public ChannelEndpoint[] ChannelEndpoints { get; init; } = [];
    public SubAgentDefinition[] SubAgents { get; init; } = [];
    public PatchableModel[] PatchableModels { get; init; } = [];
    public AttachmentConfiguration Attachments { get; init; } = new();
    public ReadImageConfiguration ReadImages { get; init; } = new();
    public RetentionSettings Retention { get; init; } = new();
    public OutpostConfiguration Outposts { get; init; } = new();
    public LemonadeChatConfiguration LemonadeChat { get; init; } = new();
}

// The Lemonade chat host: somebody's own box on the local network, outside the compose stack, and
// possibly off. Its address is the one per-deployment value, and empty means the feature does not
// exist. The key is a secret and optional — a box that checks none needs none.
public record LemonadeChatConfiguration
{
    public string ApiUrl { get; [UsedImplicitly] init; } = "";
    public string? ApiKey { get; [UsedImplicitly] init; }
}

public record OutpostConfiguration
{
    // The one value on either side of an outpost's life that is a secret, so the only one that
    // arrives as an environment variable rather than as configuration or a flag. Empty means no
    // machine may attach: an unset secret refuses every registration rather than accepting any,
    // because a deployment that forgot to set it must not be one anyone on the network can join.
    public string SharedSecret { get; [UsedImplicitly] init; } = "";
}

public record AttachmentConfiguration
{
    // How far back an attachment stays visible to the model, counted in messages. Trading token
    // cost against how long follow-up questions about a photo keep working.
    public int HydrationDepthMessages { get; [UsedImplicitly] init; } = AttachmentHydration.DefaultDepthMessages;
}

// A single-host tunable, so it lives here alone: no compose entry and no shared policy file, because
// nothing outside this process has to agree about how large an image the agent will look at.
public record ReadImageConfiguration
{
    // How large one image the model reads may be before it is refused rather than shown. Images are
    // never downscaled or re-encoded — re-encoding would silently change what the model is looking
    // at — so the only thing this bounds is whether one turn carries the file at all.
    public long MaxInlineBytes { get; [UsedImplicitly] init; } = ReadImageSupport.DefaultMaxBytes;
}

public record OpenRouterConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string ApiKey { get; [UsedImplicitly] init; }
    public int? MaxContextTokens { get; [UsedImplicitly] init; }
    public ProviderRouting? ProviderRouting { get; [UsedImplicitly] init; }
}

public record RedisConfiguration
{
    public required string ConnectionString { get; [UsedImplicitly] init; }
}

public record ChannelEndpoint
{
    public required string ChannelId { get; init; }
    public required string Endpoint { get; init; }

    // Attach-only channels (e.g. voice) cannot own a conversation; delivery fan-out
    // orders them last so a topic-owning channel anchors the shared conversation id.
    public bool AttachOnly { get; init; }
}