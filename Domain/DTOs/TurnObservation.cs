namespace Domain.DTOs;

// One turn as it left for the model and as it came back: the assembled system prompt it carried,
// and the route that served it. Null on either half is honest — a caller-supplied option set
// carries no instructions, and a turn that never reached a provider has no route.
public sealed record TurnObservation(string? SystemPrompt, ServedRoute? Route);