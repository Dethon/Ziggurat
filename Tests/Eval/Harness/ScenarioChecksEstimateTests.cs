using Shouldly;

namespace Tests.Eval.Harness;

// A sugary turn's answer, checked the way the server reads it: parsed whole against the schema,
// then held to what the scenario says the estimate must leave out. Deterministic, because whether
// a food is on the list is a fact about the parsed items and not a judgement.
public class ScenarioChecksEstimateTests
{
    private static Scenario Counted() => new()
    {
        Name = "a counted can is not listed again",
        AgentId = "sugary",
        Turn = new EvalTurn { Text = "a burger and a can", Sender = "sugary-app", Channel = "sugary" },
        Instant = EvalInstant.Evening,
        CallCeiling = 2,
        Estimate = new EstimateExpectation { NeverListed = ["coca", "coke", "cola"] }
    };

    private static string Answer(string items, string notes = "") =>
        $$"""{"items":[{{items}}],"questions":[],"notes":[{{notes}}]}""";

    private static string Item(string name) =>
        $$"""{"name":"{{name}}","portion":"one","gramsLow":100,"grams":150,"gramsHigh":200,"carbsPer100g":30,"confidence":"medium","note":null}""";

    [Fact]
    public void AnEstimateThatListsACountedFood_Fails_AndNamesIt()
    {
        var failures = ScenarioChecks.Failures(Counted(), new Recording
        {
            Answer = Answer($"{Item("beef burger")},{Item("Coca-Cola, 330 ml can")}")
        });

        failures.ShouldHaveSingleItem().ShouldContain("Coca-Cola, 330 ml can");
    }

    [Fact]
    public void AnEstimateThatLeavesItOut_Passes_EvenWhenANoteMentionsIt()
    {
        ScenarioChecks.Failures(Counted(), new Recording
        {
            Answer = Answer(Item("beef burger"), "\"The cola you counted is not included.\"")
        }).ShouldBeEmpty();
    }

    [Fact]
    public void AnAnswerTheServerCannotRead_Fails()
    {
        ScenarioChecks.Failures(Counted(), new Recording { Answer = "About 60 g, without the Coke." })
            .ShouldHaveSingleItem().ShouldContain("could not be read");
        ScenarioChecks.Failures(Counted(), new Recording())
            .ShouldHaveSingleItem().ShouldContain("no answer");
    }

    [Fact]
    public void AScenarioThatExpectsNoEstimate_DoesNotReadTheAnswer()
    {
        ScenarioChecks.Failures(Counted() with { Estimate = null }, new Recording { Answer = "prose" })
            .ShouldBeEmpty();
    }
}