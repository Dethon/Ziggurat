using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A household's own servers are its own devices: a question a turn asks is offered on the
// deployment's channels and the asker's household's own connections, and never on another
// household's server of the same name.
public class ChatMonitorHouseholdApprovalTests
{
    private static readonly Account _lucia = new("casa", "0123456789abcdef0123456789abcdef");

    [Fact]
    public async Task AnApproval_IsOfferedOnTheDeploymentsChannelsAndTheAskersHouseholds_NeverAnothers()
    {
        var casaVoice = new FakeChannelConnection { ChannelId = "voice", Household = ChannelHousehold.Own("casa", home: false) };
        var casaLibrary = new FakeChannelConnection { ChannelId = "library", Household = ChannelHousehold.Own("casa", home: false) };
        var homeLibrary = new FakeChannelConnection { ChannelId = "library", Household = ChannelHousehold.Own("home", home: true) };
        var webchat = new FakeChannelConnection { ChannelId = "signalr" };
        casaVoice.WriteMessage(MonitorTestMocks.CreateChannelMessage(channelId: "voice", agentId: "jonas") with
        {
            Principal = new Principal("voice", _lucia.UserId, _lucia)
        });
        foreach (var channel in new[] { casaVoice, casaLibrary, homeLibrary, webchat })
        {
            channel.Complete();
        }

        var factory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());
        await new ChatMonitor(
            [casaVoice, casaLibrary, homeLibrary, webchat], factory, MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object, null, NullLogger<ChatMonitor>.Instance,
            TestAssertions.Verifier()).Monitor(CancellationToken.None);

        var turn = new ConversationContext("jonas", "conv-1", _lucia.UserId, new ReplyTarget("voice", "conv-1"),
            Principal: new Principal("voice", _lucia.UserId, _lucia));
        await factory.Created.ShouldHaveSingleItem().ApprovalHandler.RequestApprovalAsync(
            ApprovalAsk.For("conv-1", [new ToolApprovalRequest("m", "mcp__shop__buy", new Dictionary<string, object?>())], turn),
            CancellationToken.None);

        casaVoice.AskedApprovals.ShouldHaveSingleItem();
        webchat.OfferedApprovals.ShouldHaveSingleItem();
        casaLibrary.OfferedApprovals.ShouldHaveSingleItem();
        homeLibrary.OfferedApprovals.ShouldBeEmpty();
    }

    // The question a turn asks is held to its asker's standing as the turn is: a member disabled
    // since their turn was admitted has an approval refused, wherever it was answered.
    [Fact]
    public async Task AnApprovalOfAMemberDisabledSinceTheirTurn_IsRefused()
    {
        var webchat = new FakeChannelConnection { ChannelId = "signalr" };
        webchat.WriteMessage(MonitorTestMocks.CreateChannelMessage(channelId: "signalr", agentId: "jonas") with
        {
            Principal = new Principal("signalr", _lucia.UserId, _lucia)
        });
        webchat.Complete();
        var standing = new SwitchableStanding();

        var factory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());
        await new ChatMonitor(
            [webchat], factory, MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object, null, NullLogger<ChatMonitor>.Instance,
            TestAssertions.Verifier(), standing: standing).Monitor(CancellationToken.None);
        standing.Now = AccountStanding.Disabled;

        var turn = new ConversationContext("jonas", "conv-1", _lucia.UserId, new ReplyTarget("signalr", "conv-1"),
            Principal: new Principal("signalr", _lucia.UserId, _lucia));
        var answer = await factory.Created.ShouldHaveSingleItem().ApprovalHandler.RequestApprovalAsync(
            ApprovalAsk.For("conv-1", [new ToolApprovalRequest("m", "mcp__shop__buy", new Dictionary<string, object?>())], turn),
            CancellationToken.None);

        webchat.AskedApprovals.ShouldHaveSingleItem();
        answer.ShouldBe(ToolApprovalResult.Rejected);
    }

    private sealed class SwitchableStanding : IAccountStanding
    {
        public AccountStanding Now { get; set; } = AccountStanding.Active;

        public Task<AccountStanding> StandingAsync(Account account, CancellationToken ct) => Task.FromResult(Now);
    }
}