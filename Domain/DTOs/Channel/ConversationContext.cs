using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// ConfigPatchModel is the model the turn asked for, as its config patch named it and before any
// host has been chosen — the same reading the recall hook and the skill preload take. It rides
// here so that a server answering one of the turn's calls can see the turn was addressed to the
// local box (a `lemonade/` id) and send nothing about it to a hosted judge.
[PublicAPI]
public record ConversationContext(
    string AgentId,
    string ConversationId,
    string UserId,
    ReplyTarget Origin,
    string? ConfigPatchModel = null);