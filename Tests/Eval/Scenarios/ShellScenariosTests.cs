using System.Text.Json;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// A scenario's required call is a claim about what the model must do, so a matcher narrower than
// the claim reports a right answer as a red. Each test here is a command an armed run sent that
// did what the scenario asks.
public class ShellScenariosTests
{
    private static bool Required(Scenario scenario, string label, string command)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path = "/sandbox", command }));
        return scenario.Required.Single(call => call.Label == label).Arguments
            .All(matcher => matcher.Matches(arguments.RootElement));
    }

    // gpt-6-luna, 2026-10-04: silenced from the timers' directory, then listed what was left by
    // reading each timer's status — one command, the claim exactly, with no ls, find or cat in it.
    [Fact]
    public void ADismissThenAReadOfEachTimersStatus_IsTheScript() =>
        Required(ShellScenarios.ADismissInsideAScript, "script",
                "cd /timers && ./dismiss && for f in */status.json; do " +
                "remaining=$(jq -r '.remainingSeconds' \"$f\"); echo \"${f%/status.json} $remaining\"; done")
            .ShouldBeTrue();

    [Fact]
    public void ADismissAlone_IsNotTheScript() =>
        Required(ShellScenarios.ADismissInsideAScript, "script", "/timers/dismiss").ShouldBeFalse();
}