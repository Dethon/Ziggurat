using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// What raised an agent-initiated message. `ScheduleId` keeps its position and its wire name because
// every emitter and every reader already spells it; a watch fills `WatchId` instead and leaves the
// schedule slot null, so nothing that reads a schedule id can mistake one for the other. `Title` is
// what a conversation minted for the fire is named after — a watch has a human name, a schedule
// never had one and keeps its old label.
[PublicAPI]
public record MessageOrigin(MessageOriginKind Kind, string? ScheduleId, string? WatchId = null, string? Title = null);