using Dashboard.Client.Metrics;
using Dashboard.Client.Services;
using Domain.DTOs.Admin;
using Shouldly;
using Tests.Unit.Dashboard.Client.Fixtures;

namespace Tests.Unit.Dashboard.Client.Metrics;

// How the dashboard names a household where a metric carries only its id. The overview's service
// health showed a household's server as "mcp-websearch 924136bf", eight characters of an id, beside a
// picker that already named it "Home"; both now read the household list through one directory, asked
// once per tab.
public sealed class HouseholdDirectoryTests
{
    private const string HomeId = "924136bf209694f9a0ef99912735ca0a";

    private static HouseholdListEntry Household(string id, string name, bool home = false) =>
        new(id, name, home, DateTimeOffset.UnixEpoch, [], [], null, null);

    private static (HouseholdDirectory Directory, FakeApiHandler Api) Build()
    {
        var api = new FakeApiHandler();
        var http = new HttpClient(api) { BaseAddress = new Uri("https://dashboard.test/") };
        return (new HouseholdDirectory(new HouseholdsAdminService(http)), api);
    }

    [Fact]
    public async Task AHouseholdTheListNames_IsShownByItsName()
    {
        var (directory, api) = Build();
        api.AnswerFor(AdminProtocol.Households, new[] { Household(HomeId, "Home", home: true), Household("b".PadLeft(32, 'b'), "Casa Seis") });

        var names = await directory.NamesAsync();

        names.Of(HomeId).ShouldBe("Home");
        names.Of("b".PadLeft(32, 'b')).ShouldBe("Casa Seis");
    }

    [Fact]
    public async Task AHouseholdTheListDoesNotName_OrAListNotAnswered_KeepsTheShortId()
    {
        var (directory, _) = Build();

        var names = await directory.NamesAsync();

        names.Of(HomeId).ShouldBe("924136bf");
    }

    [Fact]
    public async Task TheList_IsAskedOncePerTab()
    {
        var (directory, api) = Build();
        api.AnswerFor(AdminProtocol.Households, new[] { Household(HomeId, "Home", home: true) });

        await directory.NamesAsync();
        await directory.NamesAsync();

        api.Requests.Count.ShouldBe(1);
    }
}