using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// The household's own servers as its owners manage them (.claude/rules/admin.md, "Own servers"): each
// server name that holds a live tunnel credential or has a tunnel open, with its live credentials (by
// id and when minted, never the credential) and what the agent's relay says of it now.
[PublicAPI]
public sealed record AdminServers(IReadOnlyList<AdminServer> Servers);

// Outpost is a machine's (`outpost-<NAME>`), told apart by its name as the relay tells it. Since,
// Tools and the credential in use are the relay's, while connected; Tools is null for a server that
// did not list them when asked just now.
[PublicAPI]
public sealed record AdminServer(
    string Name,
    bool IsOutpost,
    ServerConnection Connection,
    DateTimeOffset? ConnectedSince,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<AdminServerCredential> Credentials);

// InUse is the credential the server's open tunnel was opened with: the one to keep when replacing it.
[PublicAPI]
public sealed record AdminServerCredential(string Id, DateTimeOffset MintedAt, bool InUse);

// Unknown is the relay not answering — the agent down or slow — never a server that is offline.
[JsonConverter(typeof(JsonStringEnumConverter<ServerConnection>))]
public enum ServerConnection
{
    Connected,
    Offline,
    Unknown
}

// A server to mint a tunnel credential for, by name: a new one, or one already listed, whose credential
// is replaced without a gap by minting another, restarting the server with it and revoking the old.
[PublicAPI]
public sealed record ServerAddition(string Name);

// Shown once, in the answer that minted it: only its hash is kept.
[PublicAPI]
public sealed record MintedServerCredential(string ServerName, string CredentialId, string Credential);