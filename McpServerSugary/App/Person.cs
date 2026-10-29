using Domain.DTOs.Channel;
using Domain.Security;
using Microsoft.AspNetCore.Http;

namespace McpServerSugary.App;

// Who an app request acts for: the account the proxy's session check named in the three headers it
// copies onto every request, over any the client sent. Bound per request from those headers, as a
// route's parameter, and kept nowhere. The app believes them because only the proxy reaches it; a
// request without them is answered 401 before any route runs (SugaryApp), so a route never binds
// nobody.
public sealed record Person(Account Account)
{
    public static ValueTask<Person?> BindAsync(HttpContext http) => ValueTask.FromResult(Of(http.Request));

    // A header the proxy sets has one value; a request carrying two did not come from it alone.
    public static Person? Of(HttpRequest request) =>
        ForwardedAccount.From(header => request.Headers[header] is { Count: 1 } values ? values[0] : null)
            is { } forwarded
            ? new Person(new Account(forwarded.TenantId, forwarded.UserId))
            : null;
}