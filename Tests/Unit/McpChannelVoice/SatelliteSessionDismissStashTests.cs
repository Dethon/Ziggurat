using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionDismissStashTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public void TryConsumeDismissedAlert_WithinWindow_ReturnsOnceThenNull()
    {
        var session = Session();
        var now = DateTimeOffset.UtcNow;
        session.NoteDismissedAlert("alarm \"trash\"", now);

        session.TryConsumeDismissedAlert(now.AddSeconds(10)).ShouldBe("alarm \"trash\"");
        session.TryConsumeDismissedAlert(now.AddSeconds(11)).ShouldBeNull(); // single-use
    }

    [Fact]
    public void TryConsumeDismissedAlert_AfterWindow_ReturnsNull()
    {
        var session = Session();
        var now = DateTimeOffset.UtcNow;
        session.NoteDismissedAlert("alarm \"trash\"", now);

        session.TryConsumeDismissedAlert(now.AddSeconds(61)).ShouldBeNull();
    }

    [Fact]
    public void TryConsumeDismissedAlert_NothingStashed_ReturnsNull()
    {
        Session().TryConsumeDismissedAlert(DateTimeOffset.UtcNow).ShouldBeNull();
    }

    [Fact]
    public void NoteDismissals_SeveralAlerts_JoinsThemWithAnd()
    {
        var session = Session();
        var now = DateTimeOffset.UtcNow;

        session.NoteDismissals(
            [new DismissedAlert("sacar la basura", AnnounceKind.Alarm),
             new DismissedAlert("la pasta", AnnounceKind.Timer)],
            now);

        session.TryConsumeDismissedAlert(now).ShouldBe("alarm \"sacar la basura\" and timer \"la pasta\"");
    }

    // A turn with nothing dismissed must not clear a description a previous wake already stashed:
    // the fallback call site runs on every dispatched turn, so an overwrite would erase the context
    // the very transcript it is about to be attached to needs.
    [Fact]
    public void NoteDismissals_NoAlerts_LeavesTheStashAlone()
    {
        var session = Session();
        var now = DateTimeOffset.UtcNow;
        session.NoteDismissals([new DismissedAlert("sacar la basura", AnnounceKind.Alarm)], now);

        session.NoteDismissals([], now);

        session.TryConsumeDismissedAlert(now).ShouldBe("alarm \"sacar la basura\"");
    }
}