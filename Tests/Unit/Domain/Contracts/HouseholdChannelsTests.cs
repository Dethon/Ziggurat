using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Contracts;

// A channel id names a connection only together with a household: the deployment's own connection
// serves every household, a household's own serves that household, and the home's serves a turn that
// names none, which runs the home's agents.
public class HouseholdChannelsTests
{
    private sealed record Channel(string ChannelId, ChannelHousehold Household) : IHouseholdChannel;

    private static readonly Channel _webchat = new("signalr", ChannelHousehold.Every);
    private static readonly Channel _homeVoice = new("voice", ChannelHousehold.Own("home", home: true));
    private static readonly Channel _otherVoice = new("voice", ChannelHousehold.Own("casa", home: false));
    private static readonly Channel[] _all = [_webchat, _homeVoice, _otherVoice];

    [Theory]
    [InlineData("casa", "casa")]
    [InlineData("home", "home")]
    [InlineData(null, "home")]
    public void AChannelId_IsFoundAmongTheTurnsHouseholdsOwnConnections(string? turn, string expected) =>
        _all.Find("voice", turn).ShouldNotBeNull().Household.HouseholdId.ShouldBe(expected);

    [Fact]
    public void AHouseholdWithNoConnectionOfThatId_FindsNone_NeverAnothersOwn() =>
        _all.Find("voice", "elsewhere").ShouldBeNull();

    [Fact]
    public void TheDeploymentsOwnConnection_ServesEveryHousehold()
    {
        _all.Serving("casa").ShouldBe([_webchat, _otherVoice]);
        _all.Serving("home").ShouldBe([_webchat, _homeVoice]);
        _all.Serving(null).ShouldBe([_webchat, _homeVoice]);
    }

    [Fact]
    public void ATurnsHousehold_IsItsConnectionsOwn_ElseItsVerifiedPersons()
    {
        var person = new ChannelMessage
        {
            ConversationId = "c", Content = "x", Sender = "Lucía", ChannelId = "signalr",
            Principal = new Principal("signalr", "lucia", new Account("casa", "lucia"))
        };

        HouseholdChannels.HouseholdOfTurn(person, _webchat).ShouldBe("casa");
        HouseholdChannels.HouseholdOfTurn(person with { Principal = null }, _otherVoice).ShouldBe("casa");
        HouseholdChannels.HouseholdOfTurn(person with { Principal = null }, _webchat).ShouldBeNull();
    }
}