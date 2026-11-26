using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Mcp.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.App;

// Who may use the app, and whose log a request reads (user's decisions, 2026-11-19), from the household's
// people view on the request's own assertion:
//
// - Sugary is on for a person once an owner said so; somebody it is off for, who holds no grant, is
//   refused every call (403 sugary-not-yours), which the app shows as a screen of its own.
// - A holder of everyone's log names a person with ?person=<user id> and reads that person's log, on
//   a GET alone (any other method 403 viewing-read-only), and only somebody Sugary is on for in their
//   own household (else 403 not-granted, never an empty log). Their own log is as it was.
// - A holder Sugary is off for reads the others and has no log: anything else but the session and the
//   pantry is sugary-not-yours.
// - The pantry (foods, recipes, a product's or a label's reading) is the household's, whoever's log is
//   open: anyone the app admits reads and changes it, and ?person= says nothing there.
//
// Somebody the view does not list is let through to their own log: the accounts service lists every
// member not disabled, and a disabled one's session is refused before it gets here, so only a stand-in
// with no people omits one. A view that cannot be read is 503, never taken for Sugary being off.
internal static class SugaryAccess
{
    public const string PersonParameter = "person";
    public const string NotYours = "sugary-not-yours";
    public const string ReadOnly = "viewing-read-only";
    public const string NotGranted = "not-granted";
    public const string Unreadable = "people-unreadable";

    // The household's, not a person's log: the pantry and what fills it.
    private static readonly string[] _household = ["/api/foods", "/api/recipes", "/api/products", "/api/labels"];

    // A request of the app names whose log it reads in its address.
    public static Task<IResult?> AdmitAsync(HttpContext http, Person person, CancellationToken ct) =>
        AdmitAsync(http, person, http.Request.Query[PersonParameter].ToString(), ct);

    // A widget's address is the signer's own row whichever log it reads, so making or removing one for
    // somebody they may read changes nothing of that person's.
    private static bool WritesOnlyTheirOwn(HttpContext http) =>
        http.Request.Path.Equals("/api/widget", StringComparison.OrdinalIgnoreCase);

    public static async Task<IResult?> AdmitAsync(HttpContext http, Person person, string named, CancellationToken ct)
    {
        IReadOnlyList<SugaryPerson> people;
        try
        {
            people = (await CallHousehold.OfPerson(person.Account.TenantId, person.Assertion,
                http.RequestServices.GetRequiredService<IHouseholdConfiguration>()).SugaryPeopleAsync(ct)).People;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or HouseholdConfigRefusedException)
        {
            return Refuse(StatusCodes.Status503ServiceUnavailable, Unreadable,
                "Who uses Sugary could not be read just now. Try again in a moment.");
        }

        var me = people.FirstOrDefault(p => p.UserId == person.Account.UserId);
        var usesSugary = me?.UsesSugary ?? true;
        var granted = me?.ReadsEveryLog ?? false;
        if (!usesSugary && !granted)
        {
            return Refuse(StatusCodes.Status403Forbidden, NotYours, "Sugary is not on for you.");
        }

        var admitted = person with { UsesSugary = usesSugary, Readable = granted ? [.. people.Where(p => p.UsesSugary)] : [] };
        if (_household.Any(path => http.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase)))
        {
            Person.Admitted(http, admitted);
            return null;
        }

        if (named.Length == 0 || named == person.Account.UserId)
        {
            if (!usesSugary && !http.Request.Path.Equals("/api/session", StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(StatusCodes.Status403Forbidden, NotYours, "Sugary is not on for you: you have no log of your own.");
            }

            Person.Admitted(http, admitted);
            return null;
        }

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !WritesOnlyTheirOwn(http))
        {
            return Refuse(StatusCodes.Status403Forbidden, ReadOnly, "Somebody else's log is read, never written, from here.");
        }

        if (admitted.Readable.FirstOrDefault(p => p.UserId == named) is not { } other)
        {
            return Refuse(StatusCodes.Status403Forbidden, NotGranted, "That log is not one you may read.");
        }

        Person.Admitted(http, admitted with { Log = new Account(person.Account.TenantId, other.UserId) });
        return null;
    }

    private static IResult Refuse(int status, string code, string detail) =>
        Results.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}