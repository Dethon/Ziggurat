using Domain.DTOs.Channel;

namespace Domain.Contracts;

// The delegations a server keeps with the objects it fires, as the agent's operator commands reach
// them (redelegate, retire-signing-keys): listed with what each object does now, and replaced one at
// a time. Only the agent signs a delegation, and the agent checks every one at fire time, so a
// server that answers this keeps nothing it could act on by itself.
public interface IDelegationStore
{
    Task<IReadOnlyList<StoredDelegation>> ListAsync(CancellationToken ct);

    // Replaces the delegation of an object that still exists; false when there is none by that id.
    Task<bool> ReplaceAsync(string kind, string id, string token, CancellationToken ct);
}

// One object a server fires, as its delegation store lists it: its kind and id, the delegation it
// keeps (none for an object no person's write delegated), what it does now (DelegatedObject's
// canonical text), and the principal its server recorded as its writer, where that names an account —
// a label redelegate offers the operator for an object written before delegations, never evidence.
public sealed record StoredDelegation(string Kind, string Id, string? Token, string Description, Principal? Writer);