using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ActiveAlertRegistryTests
{
    private static AlertHandle Handle(CancellationTokenSource cts, string text = "alarm", params string[] satellites) =>
        new(cts, satellites, text, AnnounceKind.Alarm);

    [Fact]
    public void Acknowledge_OnAnyTargetedSatellite_CancelsTheSharedAlert()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        var handle = Handle(cts, "alarm", "kitchen-01", "bedroom-01");
        registry.Register(handle);

        var dismissed = registry.Acknowledge("bedroom-01");

        dismissed.ShouldHaveSingleItem();
        handle.IsAcknowledged.ShouldBeTrue();
        handle.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Acknowledge_RemovesEveryTargetEntry_SoASecondAckIsANoOp()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register(Handle(cts, "alarm", "kitchen-01", "bedroom-01"));

        registry.Acknowledge("kitchen-01").ShouldNotBeEmpty();
        registry.Acknowledge("bedroom-01").ShouldBeEmpty(); // already cleared by the first ack
    }

    [Fact]
    public void Acknowledge_OverlappingAlertsOnOneSatellite_CancelsAllAndReturnsEachDescription()
    {
        // The Alexa "stop" model: one wake dismisses EVERYTHING ringing on that satellite.
        var registry = new ActiveAlertRegistry();
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        var alarm = new AlertHandle(cts1, ["kitchen-01"], "Take out the trash", AnnounceKind.Alarm);
        var timer = new AlertHandle(cts2, ["kitchen-01"], "pasta", AnnounceKind.Timer);
        registry.Register(alarm);
        registry.Register(timer);

        var dismissed = registry.Acknowledge("kitchen-01");

        dismissed.Count.ShouldBe(2);
        dismissed.ShouldContain(new DismissedAlert("Take out the trash", AnnounceKind.Alarm));
        dismissed.ShouldContain(new DismissedAlert("pasta", AnnounceKind.Timer));
        alarm.Token.IsCancellationRequested.ShouldBeTrue();
        timer.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Discard_RemovesOnlyItsOwnHandle_LeavingOverlappingAlertsActive()
    {
        var registry = new ActiveAlertRegistry();
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        var first = Handle(cts1, "first", "kitchen-01");
        var second = Handle(cts2, "second", "kitchen-01");
        registry.Register(first);
        registry.Register(second);

        registry.Discard(first);

        var dismissed = registry.Acknowledge("kitchen-01");
        dismissed.ShouldHaveSingleItem();
        dismissed[0].Text.ShouldBe("second");
        first.IsAcknowledged.ShouldBeFalse();
    }

    [Fact]
    public void DismissAll_CancelsEveryActiveAlertAcrossSatellites_AndReturnsDescriptions()
    {
        // The agent-reachable "stop": exec dismiss.sh silences everything ringing anywhere.
        var registry = new ActiveAlertRegistry();
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        var alarm = new AlertHandle(cts1, ["kitchen-01", "bedroom-01"], "Take out the trash", AnnounceKind.Alarm);
        var timer = new AlertHandle(cts2, ["office-01"], "pasta", AnnounceKind.Timer);
        registry.Register(alarm);
        registry.Register(timer);

        var dismissed = registry.DismissAll();

        dismissed.Count.ShouldBe(2); // the multi-satellite alarm counts once
        dismissed.ShouldContain(new DismissedAlert("Take out the trash", AnnounceKind.Alarm));
        dismissed.ShouldContain(new DismissedAlert("pasta", AnnounceKind.Timer));
        alarm.IsAcknowledged.ShouldBeTrue();
        timer.IsAcknowledged.ShouldBeTrue();
        registry.DismissAll().ShouldBeEmpty();
    }
}