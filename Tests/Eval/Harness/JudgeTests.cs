using System.Net;
using System.Text;
using System.Text.Json;
using Infrastructure.Agents.ChatClients;
using Shouldly;
using Tests.Eval.Fixtures;

namespace Tests.Eval.Harness;

// The judge's two deterministic halves: what it shows the grading model, and what it does with
// the verdict that comes back. The grading itself is a model call and is validated the way every
// scenario is — by an armed run — but a judge that dropped the reply from its material or read
// "pass" out of garbage would fail silently forever, and these pin that it cannot.
public class JudgeTests
{
    private static Scenario TheScenario(params JudgedCheck[] judged) => new()
    {
        Name = "a judged scenario",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "resérvame el taller", Sender = "fran" },
        Instant = new DateTimeOffset(2026, 8, 17, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 3,
        WorkerAnswer = "El taller es el sábado.",
        Judged = judged
    };

    private static Recording TheRecording()
    {
        var recording = new Recording();
        recording.OnInvoked(new ToolInvocation
        {
            Sequence = 1,
            ToolName = "web_browse",
            Arguments = """{"url":"http://127.0.0.1:1234/taller/reserva"}""",
            Outcome = ToolInvocationOutcome.Completed
        });
        recording.Reply = "Reservado el sábado a las 12:00; el código es TAL-77.";
        recording.Delegations = [new Delegation("jonas-worker", "Averigua el día del taller")];
        recording.FilesBefore = new Dictionary<string, string>
        {
            ["/vault/nota.md"] = "antes", ["/vault/quieta.md"] = "igual"
        };
        recording.FilesAfter = new Dictionary<string, string>
        {
            ["/vault/nota.md"] = "después", ["/vault/quieta.md"] = "igual"
        };
        return recording;
    }

    [Fact]
    public void Material_CarriesTheRubricTheTurnTheReplyTheCallsAndTheDelegations()
    {
        var check = new JudgedCheck("web.steps-are-not-reported", "Judge whether the reply narrates steps.");

        var material = Judge.Material(TheScenario(check), TheRecording(), check);

        material.ShouldContain("Judge whether the reply narrates steps.");
        material.ShouldContain("resérvame el taller");
        material.ShouldContain("el código es TAL-77");
        material.ShouldContain("web_browse");
        material.ShouldContain("/taller/reserva");
        material.ShouldContain("jonas-worker");
        material.ShouldContain("Averigua el día del taller");
        material.ShouldContain("El taller es el sábado.");
    }

    [Fact]
    public void Material_CarriesOnlyTheFilesTheTurnChanged()
    {
        var check = new JudgedCheck("vault.edits-are-surgical", "Judge the diff.");

        var material = Judge.Material(TheScenario(check), TheRecording(), check);

        material.ShouldContain("/vault/nota.md");
        material.ShouldContain("antes");
        material.ShouldContain("después");
        material.ShouldNotContain("/vault/quieta.md");
    }

    [Fact]
    public async Task FailuresAsync_APassingVerdict_ReportsNothing()
    {
        var check = new JudgedCheck("timers.id-is-descriptive", "Judge the id.");
        var transport = new CannedJudge("""{"pass": true, "reason": "descriptive enough"}""");

        var failures = await Judge.FailuresAsync(TheScenario(check), TheRecording(), "key", transport);

        failures.ShouldBeEmpty();
        transport.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FailuresAsync_AFailingVerdict_NamesTheClaimAndTheJudgesReason()
    {
        var check = new JudgedCheck("web.steps-are-not-reported", "Judge the reply.");
        var transport = new CannedJudge("""{"pass": false, "reason": "the reply lists the clicks"}""");

        var failures = await Judge.FailuresAsync(TheScenario(check), TheRecording(), "key", transport);

        failures.Count.ShouldBe(1);
        failures[0].ShouldContain("web.steps-are-not-reported");
        failures[0].ShouldContain("the reply lists the clicks");
    }

    [Fact]
    public async Task FailuresAsync_GarbageTwice_FailsLoudlyRatherThanPassing()
    {
        // A judge that cannot answer must never count as a pass: silence grading everything green
        // is the failure mode that would hide a regression behind an outage.
        var check = new JudgedCheck("timers.id-is-descriptive", "Judge the id.");
        var transport = new CannedJudge("this is not a verdict");

        var failures = await Judge.FailuresAsync(TheScenario(check), TheRecording(), "key", transport);

        failures.Count.ShouldBe(1);
        failures[0].ShouldContain("timers.id-is-descriptive");
        failures[0].ShouldContain("could not answer");
        transport.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AConditionalJudgedCheck_IsNotGraded_WhileNothingDelegated()
    {
        // The rubric's material is the delegated prompt; on a run that did the work in place
        // there is nothing to grade, and paying a judge to fail on missing material would turn
        // the model's legitimate coin into a red run.
        var scenario = TheScenario() with
        {
            IfDelegated =
            [
                new ConditionalDelegation
                {
                    Profile = "jonas-worker",
                    Judged = [new JudgedCheck("subagents.success-criteria-are-stated", "Judge the prompt.")]
                }
            ]
        };
        var recording = TheRecording();
        recording.Delegations = [];
        var transport = new CannedJudge("""{"pass": false, "reason": "nothing to grade"}""");

        var failures = await Judge.FailuresAsync(scenario, recording, "key", transport);

        failures.ShouldBeEmpty();
        transport.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AConditionalJudgedCheck_IsGraded_OnTheRunThatDelegated()
    {
        var scenario = TheScenario() with
        {
            IfDelegated =
            [
                new ConditionalDelegation
                {
                    Profile = "jonas-worker",
                    Judged = [new JudgedCheck("subagents.success-criteria-are-stated", "Judge the prompt.")]
                }
            ]
        };
        var transport = new CannedJudge("""{"pass": false, "reason": "the prompt names no result"}""");

        var failures = await Judge.FailuresAsync(scenario, TheRecording(), "key", transport);

        failures.ShouldHaveSingleItem().ShouldContain("subagents.success-criteria-are-stated");
        using var request = JsonDocument.Parse(transport.Requests.ShouldHaveSingleItem());
        request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!
            .ShouldContain("Averigua el día del taller");
    }

    [Fact]
    public async Task FailuresAsync_SendsThePinnedModel_AndAVerdictWrappedInProseStillParses()
    {
        var check = new JudgedCheck("timers.id-is-descriptive", "Judge the id.");
        var transport = new CannedJudge("""Sure. {"pass": false, "reason": "a bare number"} — that's my verdict.""");

        var failures = await Judge.FailuresAsync(TheScenario(check), TheRecording(), "key", transport);

        failures.Count.ShouldBe(1);
        failures[0].ShouldContain("a bare number");
        using var request = JsonDocument.Parse(transport.Requests[0]);
        request.RootElement.GetProperty("model").GetString().ShouldBe(Judge.Model);
        request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!
            .ShouldContain("Judge the id.");
    }

    // OpenRouter's chat/completions shape with one canned assistant message, recording every
    // request body it was sent.
    [Fact]
    public async Task FailuresAsync_RateLimitedThreeTimesThenAnswered_UsesTheVerdict()
    {
        var check = new JudgedCheck("web.steps-are-not-reported", "Judge whether the reply narrates steps.");
        var transport = new CannedJudge(
            """{"pass": false, "reason": "the reply lists the clicks"}""", rateLimitedResponses: 3);

        var failures = await Judge.FailuresAsync(TheScenario(check), TheRecording(), "key", transport);

        failures.ShouldHaveSingleItem().ShouldContain("the reply lists the clicks");
        transport.Requests.Count.ShouldBe(4);
    }

    private sealed class CannedJudge(string verdict, int rateLimitedResponses = 0) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            if (Requests.Count <= rateLimitedResponses)
            {
                // Retry-After: 0 keeps the test instant while exercising the provider-hint path.
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }

            var body = new
            {
                choices = new[] { new { message = new { content = verdict } } }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
        }
    }
}