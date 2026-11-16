using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// A channel's request that the conversation this message belongs to be shown somewhere it can be
// reviewed: the deployment's review host (WebChat) attaches to it as a later target of the turn's
// fan-out, under the title given, and the channel that sent the message stays the first target. The
// title is the only thing the host cannot work out for itself — the message's text is the turn's
// prompt, not a name for the conversation.
[PublicAPI]
public record ReviewCopy
{
    public required string Title { get; init; }

    // The conversation is several people's — a Telegram group — whoever's turn this one is. A fact
    // about the conversation, never where its copy goes: the host makes a group's copy the
    // household's rather than the turn's person's, since the others in the group wrote in it too.
    public bool Group { get; init; }
}