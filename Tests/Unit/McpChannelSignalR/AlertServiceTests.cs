using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// A timer ringing on its person's channels is shown on every chat client they have open, wherever
// they are, and pushed to their devices; dismissed from any of them it stops on all of them, and an
// alert that ended elsewhere stops here.
public class AlertServiceTests
{
    private static readonly Account _fran = new("home", "fran");
    private static readonly Account _laura = new("home", "laura");

    private readonly Mock<IHubNotificationSender> _hub = new();
    private readonly Mock<IPushNotificationService> _push = new();
    private readonly AlertService _sut;

    public AlertServiceTests() =>
        _sut = new AlertService(_hub.Object, _push.Object, NullLogger<AlertService>.Instance);

    private static OfferAlertParams Pasta(string alertId) => new()
    {
        AlertId = alertId, Owner = _fran, Text = "pasta is ready", AgentId = "jonas"
    };

    [Fact]
    public void AnOfferedAlert_GoesToItsOwnersConnectionsAndDevices()
    {
        var alertId = Guid.NewGuid().ToString("N");

        _ = _sut.OfferAlertAsync(Pasta(alertId), CancellationToken.None);

        _hub.Verify(hub => hub.SendToGroupAsync(
                "account:fran", "OnAlertOffered",
                It.Is<AlertOfferedNotification>(n => n.AlertId == alertId && n.Text == "pasta is ready"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // Pushed whether or not a chat client is open: an alarm must reach a phone in a pocket.
        _push.Verify(push => push.SendToPersonAsync(
                "fran", SpaceConfig.DefaultSlug, It.IsAny<string>(), "pasta is ready", It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _sut.OfferedTo(_fran).ShouldHaveSingleItem().AlertId.ShouldBe(alertId);
        _sut.OfferedTo(_laura).ShouldBeEmpty();
    }

    [Fact]
    public async Task ItsOwnersDismissal_IsTheOffersAnswer_AndTakesItOffEveryOneOfTheirClients()
    {
        var alertId = Guid.NewGuid().ToString("N");
        var offered = _sut.OfferAlertAsync(Pasta(alertId), CancellationToken.None);

        (await _sut.DismissAsync(alertId, _fran)).ShouldBeTrue();

        (await offered).ShouldBe(ChannelProtocol.AlertDismissed);
        _hub.Verify(hub => hub.SendToGroupAsync(
                "account:fran", "OnAlertSettled",
                It.Is<AlertSettledNotification>(n => n.AlertId == alertId && n.Dismissed),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _sut.OfferedTo(_fran).ShouldBeEmpty();
    }

    // An alert is its person's: somebody else's dismissal answers nothing and it keeps ringing.
    [Fact]
    public async Task AnotherPersonsDismissal_IsRefused_AndTheAlertKeepsRinging()
    {
        var alertId = Guid.NewGuid().ToString("N");
        var offered = _sut.OfferAlertAsync(Pasta(alertId), CancellationToken.None);

        (await _sut.DismissAsync(alertId, _laura)).ShouldBeFalse();
        (await _sut.DismissAsync(alertId, new Account("elsewhere", "fran"))).ShouldBeFalse();

        offered.IsCompleted.ShouldBeFalse();
        _sut.OfferedTo(_fran).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnAlertSettledElsewhere_IsNoAnswerHere_AndIsTakenOffItsOwnersClients()
    {
        var alertId = Guid.NewGuid().ToString("N");
        var offered = _sut.OfferAlertAsync(Pasta(alertId), CancellationToken.None);

        await _sut.SettleAlertAsync(alertId, dismissed: true);

        (await offered).ShouldBe(ChannelProtocol.NoAnswer);
        _hub.Verify(hub => hub.SendToGroupAsync(
                "account:fran", "OnAlertSettled",
                It.Is<AlertSettledNotification>(n => n.AlertId == alertId && n.Dismissed),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _sut.OfferedTo(_fran).ShouldBeEmpty();
        (await _sut.DismissAsync(alertId, _fran)).ShouldBeFalse();
    }

    // The relay calling the offer off — it rang out, or the agent is stopping — takes it down too.
    [Fact]
    public async Task AnOfferCalledOff_IsTakenOffItsOwnersClients()
    {
        var alertId = Guid.NewGuid().ToString("N");
        using var calling = new CancellationTokenSource();
        var offered = _sut.OfferAlertAsync(Pasta(alertId), calling.Token);

        await calling.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(offered);
        _hub.Verify(hub => hub.SendToGroupAsync(
                "account:fran", "OnAlertSettled",
                It.Is<AlertSettledNotification>(n => n.AlertId == alertId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _sut.OfferedTo(_fran).ShouldBeEmpty();
    }
}