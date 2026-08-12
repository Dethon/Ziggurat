using Domain.DTOs.Channel;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

public sealed record TopicsState
{
    public TopicPaging Paging { get; init; } = TopicPaging.Empty;

    public IReadOnlyList<StoredTopic> Topics => Paging.Topics;
    public string? SelectedTopicId { get; init; }
    public IReadOnlyList<AgentCatalogEntry> Agents { get; init; } = [];
    public string? SelectedAgentId { get; init; }
    // Which range of the index the list is being read from. Not a property of any topic — see
    // ADR 0024 and `Archived` in CONTEXT.md.
    public bool ShowingArchived { get; init; }

    public bool IsLoading { get; init; }
    public string? Error { get; init; }

    public static TopicsState Initial => new();
}