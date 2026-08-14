using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Agent.Settings;

public record AgentSettings
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required RedisConfiguration Redis { get; init; }
    public required AgentDefinition[] Agents { get; [UsedImplicitly] init; }
    public ChannelEndpoint[] ChannelEndpoints { get; init; } = [];
    public SubAgentDefinition[] SubAgents { get; init; } = [];
    public PatchableModel[] PatchableModels { get; init; } = [];
    public AttachmentConfiguration Attachments { get; init; } = new();
    public RetentionSettings Retention { get; init; } = new();
}

public record AttachmentConfiguration
{
    // How far back an attachment stays visible to the model, counted in messages. Trading token
    // cost against how long follow-up questions about a photo keep working.
    public int HydrationDepthMessages { get; [UsedImplicitly] init; } = AttachmentHydration.DefaultDepthMessages;
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