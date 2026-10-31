using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

// What a fire carries to act for a person: the delegation the agent signed when that person's turn
// wrote the object, as its server stored it, and what the object does now as that server describes
// it (DelegatedObject's canonical text). The agent believes neither until it has checked the token's
// signature, that it names this fire's object, that the description is the one it signed a digest
// of, and that the fire does what the description says.
[PublicAPI]
public record FireDelegation(string Token, string Object);