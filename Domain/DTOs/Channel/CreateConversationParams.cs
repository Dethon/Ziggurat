using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record CreateConversationParams
{
    public required string AgentId { get; init; }
    public required string TopicName { get; init; }
    public required string Sender { get; init; }

    // Text of the agent-initiated message that triggered this conversation —
    // distinct from TopicName, which is the sidebar label. WebChat seeds the
    // user-role prompt bubble from this so scheduled fires don't display the
    // topic title in place of the actual prompt.
    public string? InitialPrompt { get; init; }

    // Who the turn that opens the conversation acts for, which decides whose topic it is: a
    // person's account gives a topic that person owns, and a turn acting for no account — the voice
    // guest's — gives one the household shares.
    public Principal? Principal { get; init; }

    // The household a turn acting for no account is of, as its verified identity names it: the voice
    // guest's, who is somebody of the household with no account. A person's household is their
    // account's; a turn acting for nobody has none, and its topic is then nobody's to see.
    public string? GuestTenantId { get; init; }

    // The conversation is several people's (a Telegram group's), so its topic is the household's
    // whoever's turn opened it: the others in the group wrote in it too.
    public bool Group { get; init; }
}