using Domain.Contracts;
using Mcp.Hosting;
using McpServerSugary.App;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.Store;

// Which day it is at home: a period or a note is a day on her calendar, and whether that day is
// still to come is a question for the home's clock, not the server's. A time told back to her is
// told on the same clock.
//
// The server's own instance is on the deployment's zone, which is what the pen reminders run on:
// they wake for no request and nobody's call, so no household's settings can be proved for them. A
// route's calendar is bound per request (BindAsync) on the zone of the household of the person the
// request's assertion names, proved to the accounts service with that very assertion.
public sealed class HomeCalendar(TimeProvider clock, TimeZoneInfo zone)
{
    private readonly TimeProvider _clock = clock;
    private readonly TimeZoneInfo _zone = zone;

    public DateTimeOffset Now => _clock.GetUtcNow();

    public TimeZoneInfo Zone => _zone;

    // The same clock on another household's zone: the pen reminders read each owner's day on theirs.
    public HomeCalendar In(TimeZoneInfo zone) => new(_clock, zone);

    public DateOnly Today => DateOnly.FromDateTime(AtHome(Now).DateTime);

    public DateTimeOffset AtHome(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _zone);

    // Minimal APIs prefer this to the container for a route's parameter, so no route reads the day on
    // the server's clock for a person whose household keeps another.
    public static async ValueTask<HomeCalendar?> BindAsync(HttpContext http)
    {
        var server = http.RequestServices.GetRequiredService<HomeCalendar>();
        if (Person.Of(http) is not { } person)
        {
            return server;
        }

        var household = CallHousehold.OfPerson(
            person.Account.TenantId, person.Assertion, http.RequestServices.GetRequiredService<IHouseholdConfiguration>());
        return new HomeCalendar(server._clock, await household.ZoneAsync(server._zone, http.RequestAborted));
    }
}