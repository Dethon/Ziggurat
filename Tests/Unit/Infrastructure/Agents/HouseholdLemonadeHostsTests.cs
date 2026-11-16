using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Unit.Infrastructure.Agents.ChatClients;

namespace Tests.Unit.Infrastructure.Agents;

// Which Lemonade box a household's turns may run on: its own, saved on its Keys page; for the home,
// with none of its own, the deployment's configured box; for any other household with none, nothing.
// The boxes are read from the agent's lemonade-boxes view, again on every refresh, so a box an owner
// adds or removes reaches the agent without a restart.
public class HouseholdLemonadeHostsTests
{
    private const string Home = "0000000000000000000000000000aaaa";
    private const string Kapoor = "0000000000000000000000000000bbbb";
    private const string Other = "0000000000000000000000000000cccc";

    private static readonly LemonadeChatHostOptions _deployment = new() { ApiUrl = "http://lemonade-box.lan:8001/api/v1" };

    private readonly List<HouseholdLemonadeBox> _boxes = [];

    private readonly LemonadeResponsesSseHandler _kitBox = new();
    private readonly List<string> _kitTransports = [];

    private HouseholdLemonadeHosts Hosts()
    {
        var configuration = new Mock<IHouseholdConfiguration>();
        configuration.Setup(c => c.LemonadeBoxesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => [.. _boxes]);
        configuration.Setup(c => c.ForAgentAsync(HouseholdConfigProtocol.HomeHousehold, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentHouseholdConfig(Home, true, new HouseholdSettings(), new AgentsSection(), new FeaturesSection(),
                new SpacesSection(), null));
        return new HouseholdLemonadeHosts(_deployment, new FixedLemonadeModelSource([new LemonadeModel("deployments", [], null)]), configuration.Object,
            NullLoggerFactory.Instance, household =>
            {
                _kitTransports.Add(household);
                return _kitBox;
            });
    }

    [Fact]
    public async Task AHouseholdRunsOnItsOwnBox_TheHomeOnTheDeploymentsUnlessItHasOne_AndAnyOtherOnNone()
    {
        _boxes.Add(new HouseholdLemonadeBox(Kapoor, new LemonadeBox("https://box.kapoor.example/api/v1", "key")));
        var hosts = Hosts();

        await hosts.RefreshAsync(CancellationToken.None);

        hosts.For(Kapoor, home: false)!.Options.Address.ShouldBe("https://box.kapoor.example/api/v1");
        hosts.For(Kapoor, home: false)!.Options.ApiKey.ShouldBe("key");
        hosts.For(Home, home: true)!.Options.Address.ShouldBe("http://lemonade-box.lan:8001/api/v1");
        hosts.For(null, home: true)!.Options.Address.ShouldBe("http://lemonade-box.lan:8001/api/v1");
        hosts.For(Other, home: false).ShouldBeNull();
    }

    // The boxes the households saved, each under its household, are what the dashboard's tiles are probed
    // from; the deployment's own box is not among them.
    [Fact]
    public async Task TheBoxesListed_AreEachHouseholdsOwn()
    {
        _boxes.Add(new HouseholdLemonadeBox(Kapoor, new LemonadeBox("https://box.kapoor.example/api/v1", "key")));
        _boxes.Add(new HouseholdLemonadeBox(Home, new LemonadeBox("http://other-box.lan:8001/api/v1", null)));
        var hosts = Hosts();

        await hosts.RefreshAsync(CancellationToken.None);

        hosts.Boxes.Select(b => (b.Household, b.Box.Options.Address)).ShouldBe(
            [(Kapoor, "https://box.kapoor.example/api/v1"), (Home, "http://other-box.lan:8001/api/v1")], ignoreOrder: true);
    }

    [Fact]
    public async Task TheHomesOwnBox_IsUsedOverTheDeployments()
    {
        _boxes.Add(new HouseholdLemonadeBox(Home, new LemonadeBox("http://other-box.lan:8001/api/v1", null)));
        var hosts = Hosts();

        await hosts.RefreshAsync(CancellationToken.None);

        hosts.For(Home, home: true)!.Options.Address.ShouldBe("http://other-box.lan:8001/api/v1");
    }

    [Fact]
    public async Task ABoxAddedOrRemoved_ReachesTheNextRefresh()
    {
        var hosts = Hosts();
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.For(Kapoor, home: false).ShouldBeNull();

        _boxes.Add(new HouseholdLemonadeBox(Kapoor, new LemonadeBox("https://box.kapoor.example/api/v1", null)));
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.For(Kapoor, home: false).ShouldNotBeNull();

        _boxes.Clear();
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.For(Kapoor, home: false).ShouldBeNull();
    }

    // The pickers are told every household's models, the home's under its id from the first refresh —
    // before any turn of the home's has named it — so a restart leaves no picker without its box.
    [Fact]
    public async Task EveryHouseholdsModels_AreListedByHousehold_TheHomesOnTheDeploymentsBox()
    {
        _boxes.Add(new HouseholdLemonadeBox(Kapoor, new LemonadeBox("https://box.kapoor.example/api/v1", "key")));
        var hosts = Hosts();

        await hosts.RefreshAsync(CancellationToken.None);

        hosts.ModelsByHousehold.Keys.ShouldBe([Kapoor, Home], ignoreOrder: true);
        hosts.ModelsByHousehold[Home].Select(m => m.Id).ShouldBe(["deployments"]);
    }

    // A box behind the household's router is reached through its own kit's tunnel: the host's
    // transport is that household's, and its address the synthetic name the relay never resolves.
    [Fact]
    public async Task AKitBox_IsReachedThroughItsHouseholdsOwnTransport()
    {
        _boxes.Add(new HouseholdLemonadeBox(Kapoor, LemonadeBox.Kit));
        var hosts = Hosts();

        await hosts.RefreshAsync(CancellationToken.None);

        var host = hosts.For(Kapoor, home: false)!;
        host.Transport.ShouldBeSameAs(_kitBox);
        host.Options.ApiKey.ShouldBeNull();
        host.Options.Address.ShouldBe($"http://lemonade.{Kapoor}.tunnel");
        _kitTransports.ShouldBe([Kapoor]);
    }

    [Fact]
    public async Task ABoxMovedBetweenAnAddressAndTheKit_GetsAFreshHostOnTheNextRefresh()
    {
        _boxes.Add(new HouseholdLemonadeBox(Kapoor, new LemonadeBox("https://box.kapoor.example/api/v1", "key")));
        var hosts = Hosts();
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.For(Kapoor, home: false)!.Transport.ShouldBeNull();

        _boxes[0] = new HouseholdLemonadeBox(Kapoor, LemonadeBox.Kit);
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.For(Kapoor, home: false)!.Transport.ShouldBeSameAs(_kitBox);

        _boxes[0] = new HouseholdLemonadeBox(Kapoor, new LemonadeBox("https://box.kapoor.example/api/v1", "key"));
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.For(Kapoor, home: false)!.Options.Address.ShouldBe("https://box.kapoor.example/api/v1");
    }
}