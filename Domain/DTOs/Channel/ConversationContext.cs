using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// ConfigPatchModel is the model the turn asked for, as its config patch named it and before any
// host has been chosen — the same reading the recall hook and the skill preload take. It rides
// here so that a server answering one of the turn's calls can see the turn was addressed to the
// local box (a `lemonade/` id) and send nothing about it to a hosted judge.
//
// OriginKind is what raised the turn when nobody sent it — a schedule, a watch, a login wake — and
// null for a person's own message, so a server can refuse what only a person present may start (a
// login handoff). Optional, so a sender from before it reads as a person's turn.
//
// Principal is who the turn acts for, as the channel that vouched for them. UserId alone cannot
// say that on a fire: a schedule's userId is whatever its JSON said and its origin is where it
// delivers, so a server that acts as a person reads the principal and nothing else.
[PublicAPI]
public record ConversationContext(
    string AgentId,
    string ConversationId,
    string UserId,
    ReplyTarget Origin,
    string? ConfigPatchModel = null,
    MessageOriginKind? OriginKind = null,
    Principal? Principal = null)
{
    // Where the approval client puts the conversation a call serves on that call's own arguments,
    // beside ToolPermission's: a tool acting outside the turn reads who it acts for from there.
    public static readonly object ContextKey = typeof(ConversationContext);
}