using System.Text.Json.Serialization;

namespace Domain.Security.Signing;

// What a turn nobody sent may act as: the agent's own word, signed while a person's turn created or
// rewrote the thing that will raise it, that this person stands behind that very object doing what it
// did then. The claims are the person's, out of the turn's verified identity, and three of its own:
// the kind of object, its id, and a digest of what it does (DelegatedObject). The server that keeps
// the object stores the token beside it and hands it back with the fire; only the agent's key signs
// one, so a server can keep a delegation but never make one.
public sealed record Delegation : IdentityClaims
{
    // Lives as long as the object does: the object's own end — deleted, or rewritten by a turn that
    // gives it another delegation or none — is what ends it, and retiring the key it was signed with
    // ends every one at once. A century is the whole of "as long as the object" a NumericDate needs.
    public static readonly TimeSpan ObjectLifetime = TimeSpan.FromDays(36525);

    public Delegation()
    {
    }

    public Delegation(IdentityClaims person, DelegatedObject what) : base(person)
    {
        Kind = what.Kind;
        ObjectId = what.Id;
        Digest = what.Digest;
    }

    [JsonPropertyName("knd")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("oid")]
    public string ObjectId { get; init; } = "";

    [JsonPropertyName("dig")]
    public string Digest { get; init; } = "";
}