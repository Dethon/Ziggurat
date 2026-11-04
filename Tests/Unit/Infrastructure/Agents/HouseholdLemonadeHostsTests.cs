using Domain.Contracts;
using Domain.DTOs.Household;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

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

    private HouseholdLemonadeHosts Hosts()
    {
        var configuration = new Mock<IHouseholdConfiguration>();
        configuration.Setup(c => c.LemonadeBoxesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => [.. _boxes]);
        return new HouseholdLemonadeHosts(_deployment, FixedLemonadeModelSource.None, configuration.Object,
            NullLoggerFactory.Instance);
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
}