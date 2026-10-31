using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace Domain.Security;

// Who the proxy's session check says a request is from: the accounts service answers forward_auth
// with these headers and the proxy copies them onto the request it passes on, over anything the
// client sent. An app believes only the signed one, the assertion (AssertionVerifier), and builds
// the person from what it signed: the plain three are what anything on the compose network could
// write, kept for whatever only reads them for show.
//
// Spelled here because every app behind the check reads them, and the accounts service's session
// check answers them by these names; the conformance check holds the proxy's copy_headers to this
// list.
//
// SessionId is the session the check signed the assertion for, where it names one: the chat hub asks
// the accounts service for its connection's person by it, so a revoked session stops starting turns.
public sealed record ForwardedAccount(string UserId, string TenantId, IReadOnlyList<string> Roles, string? SessionId = null)
{
    public const string UserHeader = "X-Ziggurat-User";
    public const string TenantHeader = "X-Ziggurat-Tenant";
    public const string RolesHeader = "X-Ziggurat-Roles";

    // The accounts service's signed assertion of the other three (Domain/Security/Signing), which
    // the proxy copies with them, and the one an app believes.
    public const string AssertionHeader = "X-Ziggurat-Assertion";

    public static IReadOnlyList<string> Headers { get; } = [UserHeader, TenantHeader, RolesHeader, AssertionHeader];

    // The role the check names beside the household role for whoever an owner granted every topic
    // in the household. Read once per connection, so a grant or a withdrawal reaches an open chat
    // client when it next connects.
    public const string SeesAllRole = "sees-all";

    // The deployment's operator, beside the household role: the dashboard is theirs alone.
    public const string OperatorRole = "operator";

    public bool IsOperator => Roles.Contains(OperatorRole);

    public bool SeesAll => Roles.Contains(SeesAllRole);

    public TopicViewer Viewer => new(UserId, TenantId, SeesAll);

    // The account the check named, as everything keyed by a person compares it.
    public Account Account => new(TenantId, UserId);

    // A turn this person sent on a channel behind the proxy: the account the check named, and the
    // account's id as the channel's own name for them, since such a channel knows them by nothing else.
    public Principal PrincipalOn(string channelId) => new(channelId, UserId, Account);
}