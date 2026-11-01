using Domain.Security;

namespace Accounts.Admin;

// Who an admin request is from: the person the session check's assertion for the admin channel
// names, verified by the gate in front of every admin route (AdminApi) before the route runs, and
// kept on that request alone. A route binds it as a parameter and so never binds nobody: a request
// with no believable assertion was answered 401 before it got there.
//
// On a household route the household is the caller's own (HouseholdId), from the signed claims and
// never from a parameter, so no route there can be pointed at another household.
public sealed record AdminCaller(ForwardedAccount Person)
{
    private static readonly object _item = new();

    public string UserId => Person.UserId;

    public string HouseholdId => Person.TenantId;

    public static ValueTask<AdminCaller?> BindAsync(HttpContext http) => ValueTask.FromResult(Of(http));

    public static AdminCaller? Of(HttpContext http) =>
        http.Items.TryGetValue(_item, out var caller) ? caller as AdminCaller : null;

    internal static void Keep(HttpContext http, AdminCaller caller) => http.Items[_item] = caller;
}