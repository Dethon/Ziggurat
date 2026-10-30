using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.App;

// Who an app request acts for: the account the proxy's session check named in the assertion it
// signs and the proxy copies onto every request, verified against the accounts service's keys and
// held to Sugary's channel. The three plain headers beside it are never read: anything on the
// compose network could write them. Verified once per request, before any route runs (SugaryApp),
// and kept on that request alone; a request with no believable assertion is answered 401 there, so
// a route never binds nobody.
public sealed record Person(Account Account)
{
    private static readonly object _item = new();

    public static ValueTask<Person?> BindAsync(HttpContext http) => ValueTask.FromResult(Of(http));

    // The person the gate in front of /api verified for this request.
    public static Person? Of(HttpContext http) =>
        http.Items.TryGetValue(_item, out var person) ? person as Person : null;

    // A header the proxy sets has one value; a request carrying two did not come from it alone.
    internal static async Task<Person?> SignedInAsync(HttpContext http, CancellationToken ct)
    {
        var assertion = http.Request.Headers[ForwardedAccount.AssertionHeader] is { Count: 1 } values ? values[0] : null;
        var account = await http.RequestServices.GetRequiredService<AssertionVerifier>()
            .VerifyAsync(assertion, SugaryChannel.ChannelId, ct);
        if (account is null)
        {
            return null;
        }

        var person = new Person(account.Account);
        http.Items[_item] = person;
        return person;
    }
}