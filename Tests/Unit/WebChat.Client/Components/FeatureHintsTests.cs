using Shouldly;
using WebChat.Client.Components.Admin;

namespace Tests.Unit.WebChat.Client.Components;

// What the features page says each feature does, read by an owner deciding whether to turn it off.
// It said "the household's Sugary log" when every person keeps their own, and that timers "ring on
// the household's devices" when a household with no voice server has nothing to ring them on and its
// countdowns are scheduled messages.
public class FeatureHintsTests
{
    [Fact]
    public void Sugary_IsEachPersonsOwnLog() =>
        FeatureHints.Sugary.ShouldContain("each person's own");

    [Fact]
    public void Timers_RingOnTheVoiceSatellites_AndOtherwiseArriveAsMessages()
    {
        FeatureHints.Timers.ShouldContain("voice satellites");
        FeatureHints.Timers.ShouldContain("message");
    }
}