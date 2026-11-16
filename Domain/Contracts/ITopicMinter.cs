using Domain.DTOs.Channel;

namespace Domain.Contracts;

// A topic minted by the agent for a conversation a channel server opens, for whoever the assertion
// names. A refusal or an agent that cannot be reached is thrown: a conversation is never opened
// under a topic that does not exist.
public interface ITopicMinter
{
    Task<TopicMinted> MintAsync(TopicMintRequest request, string? assertion, CancellationToken ct);
}