using System.Text.Json;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Scenarios;

// A required call narrower than its claim reports a right answer as a red. Each command here is
// one an armed run sent that did what the scenario asks.
public class HomeAssistantScenariosTests
{
    private const string Alarms = "/ha/entities/calendar/assistant_alarms_(assistant-alarms)";

    private static bool Required(Scenario scenario, string label, string command)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path = Alarms, command }));
        return scenario.Required.Single(call => call.Label == label).Arguments
            .All(matcher => matcher.Matches(arguments.RootElement));
    }

    // glm-5.3-flash, 2026-10-04: the move as one command, the delete chained to the create — the
    // claim exactly, in one exec rather than two.
    private const string Chained =
        "./delete_event --uid alarm-basura && ./create_event --summary \"Sacar la basura\" " +
        "--start_date_time \"2026-08-17 22:30:00\" --description '{\"target\":{\"room\":\"kitchen\"},\"insistent\":{}}'";

    [Theory]
    [InlineData("delete")]
    [InlineData("create")]
    public void ADeleteChainedToTheCreate_IsBothCalls(string label) =>
        Required(HomeAssistantScenarios.MoveTheTrashAlarm, label, Chained).ShouldBeTrue();

    [Fact]
    public void ACreateNamedOnlyInAnArgument_IsNotTheCreate() =>
        Required(HomeAssistantScenarios.MoveTheTrashAlarm, "create",
                "./get_events --summary \"create_event basura insistent 2026-08-17 22:30\"")
            .ShouldBeFalse();
}