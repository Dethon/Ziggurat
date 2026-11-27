using System.Collections.Immutable;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Streaming;

public sealed record StreamingContent
{
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public IReadOnlyList<ToolCallStep>? Tools { get; init; }
    public string? CurrentMessageId { get; init; }

    public bool HasContent =>
        !string.IsNullOrEmpty(Content) ||
        Tools is { Count: > 0 } ||
        !string.IsNullOrEmpty(Reasoning);
}

public sealed record StreamingState
{
    public ImmutableDictionary<string, StreamingContent> StreamingByTopic { get; init; }
        = ImmutableDictionary<string, StreamingContent>.Empty;

    public ImmutableHashSet<string> StreamingTopics { get; init; } = [];

    public static StreamingState Initial => new();
}