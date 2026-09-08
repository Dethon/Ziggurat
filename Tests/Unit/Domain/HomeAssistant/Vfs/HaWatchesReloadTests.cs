using System.Text.Json.Nodes;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// The config write reloads the automation, and a brand-new entity can trail the write by a
// moment. A watch created paused must still end up off, so the store asks the home again a few
// times before giving up — on the injected clock, never the wall clock.
public class HaWatchesReloadTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

    private static readonly HaWatchSpec _paused = HaWatchSpec.Parse("""
        {"name": "Laura's sugar", "enabled": false,
         "triggers": [{"trigger": "numeric_state", "entity_id": "sensor.laura_glucose", "below": 60}],
         "effects": [{"kind": "prompt", "prompt": "p"}]}
        """);

    [Fact]
    public async Task AnEntityThatTrailsTheWrite_IsStillTurnedOff_OnceItAppears()
    {
        var client = new FakeHaClient { EntityLagListings = 2 };
        var time = new ArmedClock(_now);
        var watches = new HaWatches(() => client, time);

        var write = watches.WriteAsync("sugar-low", _paused, "jonas", null, CancellationToken.None);
        var written = await Driven(write, time, delays: 2);

        client.AutomationListings.ShouldBe(3);
        client.Calls.ShouldBe([("automation", "turn_off", client.Automations["assistant_watch_sugar-low"].EntityId)]);
        client.Automations["assistant_watch_sugar-low"].IsOn.ShouldBeFalse();
        written.Enabled.ShouldBeFalse();
        written.State.ShouldNotBeNull();
    }

    [Fact]
    public async Task AnEntityThatNeverAppears_IsGivenUpOn_AndTheWatchReadsTheFilesEnabled()
    {
        var client = new FakeHaClient { EntityLagListings = 100 };
        var time = new ArmedClock(_now);
        var watches = new HaWatches(() => client, time);

        var write = watches.WriteAsync("sugar-low", _paused, "jonas", null, CancellationToken.None);
        var written = await Driven(write, time, delays: 4);

        client.AutomationListings.ShouldBe(5);
        client.Calls.ShouldBeEmpty();
        written.State.ShouldBeNull();
        written.Spec.Enabled.ShouldBeFalse();
        // The config itself was written: the home holds the automation, only its entity is late.
        client.UpsertedAutomations.Single().Config["alias"]!.GetValue<string>().ShouldBe("Laura's sugar");
    }

    [Fact]
    public async Task AnEntityThatIsThereAtOnce_IsAskedForOnce()
    {
        var client = new FakeHaClient();
        var time = new FakeTimeProvider(_now);
        var watches = new HaWatches(() => client, time);

        await watches.WriteAsync("sugar-low", _paused, "jonas", null, CancellationToken.None);

        client.AutomationListings.ShouldBe(1);
        time.GetUtcNow().ShouldBe(_now);
    }

    // The retry waits on the fake clock, so the test moves it — but only once the write has armed
    // the delay the advance is meant to end. A yield-then-advance guessed at that ordering, and when
    // the suite ran wide the guess lost: the advance fired nothing, the delay armed against a clock
    // already past it, and the test hung the whole run waiting for a write that could never finish.
    private static async Task<HaWatch> Driven(Task<HaWatch> write, ArmedClock time, int delays)
    {
        for (var step = 0; step < delays; step++)
        {
            await time.AdvancePastAsync(TimeSpan.FromMilliseconds(200), previously: step);
        }
        return await write;
    }
}