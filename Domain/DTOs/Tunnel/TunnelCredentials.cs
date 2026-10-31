using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JetBrains.Annotations;

namespace Domain.DTOs.Tunnel;

// What the relay and the accounts service say to each other about a tunnel credential, spelled once
// here so both ends read the same records: the accounts service answers them, and the agent's relay
// asks with them and listens for the revocations.
public static partial class TunnelCredentialProtocol
{
    // Under the accounts service's internal prefix, which the proxy answers 404 on every host.
    public const string VerifyPath = "/accounts/internal/tunnels/verify";

    // Where a revocation is published (a TunnelCredentialRevoked, as JSON), so the relay can close
    // the tunnel the credential opened. Pub/sub reaches only who is subscribed at that moment: the
    // relay still verifies on every open, which is what keeps a revoked credential out for good.
    public const string RevokedChannel = "accounts:tunnel-credentials:revoked";

    // A server's name goes into the synthetic address a tunnelled server is reached at, so it is a
    // DNS label: lowercase letters, digits and inner hyphens, at most 63 characters.
    public const int ServerNameMaxLength = 63;

    public static bool IsServerName(string? name) => name is not null && ServerName().IsMatch(name);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerName();
}

// The relay's question: the credential a tunnel presented, and the server name the tunnel claims.
[PublicAPI]
public sealed record TunnelCredentialQuery(string Credential, string ServerName);

// A credential that opens a tunnel for that server. The id is the credential's, not a secret: it is
// what a revocation names, so the relay closes the tunnel this credential opened and no other.
[PublicAPI]
public sealed record TunnelCredentialVerified(string CredentialId, string TenantId, string ServerName);

[PublicAPI]
public sealed record TunnelCredentialRefused(TunnelCredentialRefusal Reason);

// By name on the wire: the relay logs it and reports it on the errors feed for the operator to read.
[JsonConverter(typeof(JsonStringEnumConverter<TunnelCredentialRefusal>))]
public enum TunnelCredentialRefusal
{
    // No credential was ever minted with this value, or it was altered.
    Unknown,

    Revoked,

    // A live credential, minted for another of its household's servers than the one the tunnel says
    // it is.
    AnotherServer
}

// Published on TunnelCredentialProtocol.RevokedChannel when a credential is revoked.
[PublicAPI]
public sealed record TunnelCredentialRevoked(string CredentialId, string TenantId, string ServerName);