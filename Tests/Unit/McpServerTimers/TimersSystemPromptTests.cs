using Domain.Prompts;
using McpServerTimers.McpPrompts;
using Shouldly;

namespace Tests.Unit.McpServerTimers;

public class TimersSystemPromptTests
{
    // A prompt is fetched for nobody and cached once for every household this server serves, so it
    // carries no household's satellites: the roster is the mount's, read per call through the call's
    // household (/timers/satellites.json), and the prompt is the same static text for everyone.
    [Fact]
    public void GetTimerPrompt_CarriesNoHouseholdsRoster()
    {
        new TimersSystemPrompt().GetTimerPrompt().ShouldBe(TimerPrompt.Prompt);
    }
}