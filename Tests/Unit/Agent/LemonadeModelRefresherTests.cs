using Agent.App;
using Domain.Contracts;
using Domain.DTOs.Household;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Agent;

// The refresher's reads prove themselves to the accounts service with the agent's own token, which it checks
// against the keys the agent publishes: asked before the agent listens, its fetch finds nobody home and it
// refuses every agent token, a shared server's call tokens included, until it may fetch again.
public class LemonadeModelRefresherTests
{
    [Fact]
    public async Task NothingIsRead_UntilTheAgentListens()
    {
        var reads = 0;
        var configuration = new Mock<IHouseholdConfiguration>();
        configuration.Setup(c => c.LemonadeBoxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref reads);
                return [];
            });
        configuration.Setup(c => c.ForAgentAsync(HouseholdConfigProtocol.HomeHousehold, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentHouseholdConfig("0000000000000000000000000000aaaa", true, new HouseholdSettings(),
                new AgentsSection(), new FeaturesSection(), new SpacesSection(), null));
        var hosts = new HouseholdLemonadeHosts(new LemonadeChatHostOptions { ApiUrl = "http://lemonade-box.lan:8001/api/v1" }, new FixedLemonadeModelSource([]),
            configuration.Object, NullLoggerFactory.Instance);
        using var listening = new CancellationTokenSource();
        using var refresher = new LemonadeModelRefresher(hosts, NullLogger<LemonadeModelRefresher>.Instance, listening: listening.Token);

        await refresher.StartAsync(CancellationToken.None);
        await Eventually.Settle();
        Volatile.Read(ref reads).ShouldBe(0);

        await listening.CancelAsync();
        await Eventually.Until(() => Volatile.Read(ref reads) > 0, "the boxes are read once the agent listens");
        await refresher.StopAsync(CancellationToken.None);
    }
}