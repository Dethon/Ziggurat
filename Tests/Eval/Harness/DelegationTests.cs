using Infrastructure.Agents.ChatClients;
using Shouldly;
using Tests.Eval.Fixtures;

namespace Tests.Eval.Harness;

// With real workers nothing stands where the worker would have been, so what was delegated is
// read off the parent's own calls: the profile and the prompt are the call's arguments, and the
// worker's answer is its result.
public class DelegationTests
{
    private static ToolInvocation Call(string tool, string arguments, string? result = null) => new()
    {
        Sequence = 0,
        ToolName = tool,
        Arguments = arguments,
        Result = result,
        Outcome = ToolInvocationOutcome.Completed
    };

    [Fact]
    public void Heard_ADelegation_CarriesTheProfileThePromptAndTheWorkersAnswer()
    {
        var heard = Delegation.Heard(
        [
            Call(EvalTools.WebSearch.Replace("*", "web"), """{"query":"rifa"}"""),
            Call(EvalTools.Subagent,
                """{"subAgentId":"jonas-worker","prompt":"Averigua el total de la rifa de 2025"}""",
                """{"status":"completed","result":"Fueron 1.577 euros."}""")
        ]);

        heard.ShouldHaveSingleItem().ShouldBe(
            new Delegation("jonas-worker", "Averigua el total de la rifa de 2025") { Answer = "Fueron 1.577 euros." });
    }

    [Fact]
    public void Heard_AWorkerThatFailed_AnswersWithWhatTheToolSaid()
    {
        // A timeout is an answer the parent read and had to act on, so it is what the judge and
        // the dump are shown rather than nothing.
        var failure = """{"ok":false,"errorCode":"timeout","message":"exceeded its maximum execution time"}""";

        var heard = Delegation.Heard(
            [Call(EvalTools.Subagent, """{"subAgentId":"jonas-worker","prompt":"Lee la crónica"}""", failure)]);

        heard.ShouldHaveSingleItem().Answer.ShouldBe(failure);
    }
}