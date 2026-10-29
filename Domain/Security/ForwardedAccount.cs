using Domain.DTOs.Channel;

namespace Domain.Security;

// Who the proxy's session check says a request is from: the accounts service answers forward_auth
// with these three headers and the proxy copies them onto the request it passes on, over anything
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

    public static IReadOnlyList<string> Headers { get; } = [UserHeader, TenantHeader, RolesHeader];

    // A turn this person sent on a channel behind the proxy: the account the check named, and the
    // account's id as the channel's own name for them, since such a channel knows them by nothing else.
    public Principal PrincipalOn(string channelId) => new(channelId, UserId, new Account(TenantId, UserId));

    // All three or nobody: the check always answers all three, so a request carrying fewer did not
    // come through it.
    public static ForwardedAccount? From(Func<string, string?> header) =>
        (header(UserHeader), header(TenantHeader), header(RolesHeader)) is
        ({ Length: > 0 } user, { Length: > 0 } tenant, { Length: > 0 } roles)
            ? new ForwardedAccount(user, tenant, roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : null;
}