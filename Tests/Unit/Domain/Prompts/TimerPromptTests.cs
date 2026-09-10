using Domain.DTOs.Voice;
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// Only Build is tested: it takes a roster and composes. The idiom text it wraps is a const string,
// and asserting that a constant contains fragments of itself tests nothing.
public class TimerPromptTests
{
    [Fact]
    public void Build_WithRoster_ListsSatelliteIdsAndRoomsAfterTheIdiomText()
    {
        var result = TimerPrompt.Build(
            [new SatelliteDescriptor("kitchen-01", "Kitchen"), new SatelliteDescriptor("fran-office-01", "Fran's office")]);

        // The roster lets a text-channel agent OFFER the rooms instead of asking blind.
        result.ShouldContain("kitchen-01 — Kitchen");
        result.ShouldContain("fran-office-01 — Fran's office");
        result.ShouldContain(TimerPrompt.Prompt);
    }

    [Fact]
    public void Build_EmptyRoster_IsExactlyTheRosterlessPrompt()
    {
        // Fail-open shape: when the hub cannot be asked at prompt-fetch time the prompt degrades
        // to the static idiom text, which already tells the agent to ask which room.
        TimerPrompt.Build([]).ShouldBe(TimerPrompt.Prompt);
    }
}