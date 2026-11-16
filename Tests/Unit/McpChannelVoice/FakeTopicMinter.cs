using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;

namespace Tests.Unit.McpChannelVoice;

// The agent's topic-minting route as the voice server sees it: every mint answered with a fresh
// topic over a fresh conversation, and remembered with the assertion it carried, or refused whole
// while it is down.
internal sealed class FakeTopicMinter : ITopicMinter
{
    private readonly Lock _gate = new();
    private readonly List<(TopicMintRequest Request, string? Assertion)> _minted = [];
    private Exception? _outage;

    public IReadOnlyList<(TopicMintRequest Request, string? Assertion)> Minted
    {
        get
        {
            lock (_gate)
            {
                return [.. _minted];
            }
        }
    }

    public FakeTopicMinter Down(Exception? outage = null)
    {
        _outage = outage ?? new HttpRequestException("agent unreachable");
        return this;
    }

    public Task<TopicMinted> MintAsync(TopicMintRequest request, string? assertion, CancellationToken ct)
    {
        if (_outage is not null)
        {
            return Task.FromException<TopicMinted>(_outage);
        }

        int count;
        lock (_gate)
        {
            _minted.Add((request, assertion));
            count = _minted.Count;
        }

        var topicId = $"topic-{count}";
        return Task.FromResult(new TopicMinted(topicId, ConversationIdGenerator.CreateFor(topicId).ConversationId));
    }
}