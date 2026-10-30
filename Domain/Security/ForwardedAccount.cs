using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace Domain.Security;

// Who the proxy's session check says a request is from: the accounts service answers forward_auth
// with these headers and the proxy copies them onto the request it passes on, over anything
// the client sent. An app believes them because only the proxy reaches it; one that is reached
// without them knows nobody.
//
// Spelled here because every app behind the check reads them and none references the accounts
// service, which spells them again for itself; the conformance check holds the proxy's copy_headers
// to this list.
public sealed record ForwardedAccount(string UserId, string TenantId, IReadOnlyList<string> Roles)
{
    public const string UserHeader = "X-Ziggurat-User";
    public const string TenantHeader = "X-Ziggurat-Tenant";
    public const string RolesHeader = "X-Ziggurat-Roles";

    // The accounts service's signed assertion of the other three (Domain/Security/Signing), which
    // the proxy copies with them. Copied but not yet read here: an app verifies it against the
    // service's published keys.
    public const string AssertionHeader = "X-Ziggurat-Assertion";

    public static IReadOnlyList<string> Headers { get; } = [UserHeader, TenantHeader, RolesHeader, AssertionHeader];

    // The role the check names beside the household role for whoever an owner granted every topic
    // in the household. Read once per connection, so a grant or a withdrawal reaches an open chat
    // client when it next connects.
    public const string SeesAllRole = "sees-all";

    public bool SeesAll => Roles.Contains(SeesAllRole);

    public TopicViewer Viewer => new(UserId, TenantId, SeesAll);

    // The account the check named, as everything keyed by a person compares it.
    public Account Account => new(TenantId, UserId);

    // A turn this person sent on a channel behind the proxy: the account the check named, and the
    // account's id as the channel's own name for them, since such a channel knows them by nothing else.
    public Principal PrincipalOn(string channelId) => new(channelId, UserId, Account);

    // All three or nobody: the check always answers all three, so a request carrying fewer did not
    // come through it.
    public static ForwardedAccount? From(Func<string, string?> header) =>
        (header(UserHeader), header(TenantHeader), header(RolesHeader)) is
        ({ Length: > 0 } user, { Length: > 0 } tenant, { Length: > 0 } roles)
            ? new ForwardedAccount(user, tenant, roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : null;
}