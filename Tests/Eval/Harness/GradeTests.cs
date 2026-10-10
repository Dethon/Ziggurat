using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Tests.Eval.Harness;

// The half of a scenario that is a measurement rather than a contract. A pass says a run stayed
// inside what the scenario tolerates; a grade says how good the answer was, on a scale two models
// can be compared along. Facts are counted — the corpus is planted, so what is true is a list —
// and only what a list cannot say is put to the judge.
public class GradeTests
{
    private static readonly Grade _facts = Grade.Facts("accuracy",
        new SpokenValue("the 2024 total", "1.316", "1316"),
        new SpokenValue("the 2025 total", "1.577", "1577"),
        new SpokenValue("the difference", "261"),
        new SpokenValue("the year it rose", "2025"));

    private static Scenario TheScenario(params Grade[] graded) => new()
    {
        Name = "a graded scenario",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "¿cuánto subió la rifa?", Sender = "fran" },
        Instant = new DateTimeOffset(2026, 8, 17, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 12,
        Graded = graded
    };

    [Theory]
    [InlineData("Fueron 1.316 en 2024 y 1.577 en 2025: 261 más.", 10)]
    [InlineData("En 2025 se recaudaron 1577 euros.", 5)]
    [InlineData("No lo he encontrado.", 0)]
    public void Facts_ScoreTheShareOfPlantedValuesTheReplyCarries(string reply, double expected)
    {
        ((FactGrade)_facts).Score(reply).ShouldBe(expected, 0.001);
    }

    [Fact]
    public void Facts_AWrongValueBesideARightOne_CostsNothingHere()
    {
        // "Antes 1.200, ahora 1.577" is a good answer, and a reply that names the stale figure to
        // set it aside must not be marked with one that believed it. Whether a reply was fooled
        // is the judged dimension's question; this one only counts what is true and present.
        ((FactGrade)_facts).Score("En 2025 fueron 1.577, no los 1.200 que dice el buscador.")
            .ShouldBe(5, 0.001);
    }

    private static readonly Source _presentation = new("/solar", "Sol de la Vega: la cooperativa solar del vecindario");
    private static readonly Source _report = new("/solar/memoria-2025", "Memoria anual 2025");
    private static readonly GroundedGrade _grounded =
        (GroundedGrade)Grade.Grounded("grounding", _presentation, _report);

    private static Recording Having(string reply, params string[] browsed)
    {
        var recording = new Recording { Parent = ["Jonas"], Reply = reply };
        browsed
            .Select((url, index) => new Infrastructure.Agents.ChatClients.ToolInvocation
            {
                Sequence = index,
                ToolName = "mcp__web__web_browse",
                Arguments = $$"""{"url":"{{url}}","maxLength":10000}""",
                Outcome = Infrastructure.Agents.ChatClients.ToolInvocationOutcome.Completed,
                // The second one is a worker's: a page a worker opened was opened.
                AgentId = index == 1 ? "Jonas Worker" : "Jonas"
            })
            .ToList()
            .ForEach(recording.OnInvoked);
        return recording;
    }

    // Grounding counted, not judged: a page a right answer rests on is grounded when the reply
    // names it and somebody opened it. The judge gave nine or ten to every reply it was shown,
    // which is a third of the judge's bill for a column that separated nothing.
    [Fact]
    public void Grounded_APageCitedAndOpened_Counts_AndEachOfTheTwoAloneDoesNot()
    {
        var both = Having(
            "Son 237 socios ([memoria](http://127.0.0.1:4000/solar/memoria-2025)); antes 184 ([presentación](http://127.0.0.1:4000/solar)).",
            "http://127.0.0.1:4000/solar", "http://127.0.0.1:4000/solar/memoria-2025");
        _grounded.Score(both).ShouldBe(10);

        // Cited from the search snippet, never opened.
        var unopened = Having(
            "Son 237 ([memoria](http://127.0.0.1:4000/solar/memoria-2025)); antes 184 ([presentación](http://127.0.0.1:4000/solar)).",
            "http://127.0.0.1:4000/solar/memoria-2025");
        _grounded.Score(unopened).ShouldBe(5);

        // Opened and left out of the reply.
        var uncited = Having(
            "Son 237 socios.", "http://127.0.0.1:4000/solar", "http://127.0.0.1:4000/solar/memoria-2025");
        _grounded.Score(uncited).ShouldBe(0);
    }

    [Fact]
    public void Grounded_APathInsideALongerOne_IsNotThatPage()
    {
        // "/solar" is the start of every page on the site: a reply that cites the report has
        // not cited the presentation, and a browse of the report has not opened it.
        var recording = Having(
            "Son 237 socios ([memoria](http://127.0.0.1:4000/solar/memoria-2025)).",
            "http://127.0.0.1:4000/solar/memoria-2025");

        _grounded.Score(recording).ShouldBe(5);
    }

    [Fact]
    public void Grounded_APageNamedByItsTitle_IsCited()
    {
        // A spoken-style reply names the page instead of linking it, and that is a citation.
        var recording = Having(
            "Según la Memoria anual 2025 son 237 socios; la presentación de 2024 decía 184.",
            "http://127.0.0.1:4000/solar", "http://127.0.0.1:4000/solar/memoria-2025");

        _grounded.Score(recording).ShouldBe(5);
    }

    [Fact]
    public async Task AGroundedDimension_IsMarkedWithoutTheJudge()
    {
        var recording = Having(
            "Son 237 ([memoria](http://127.0.0.1:4000/solar/memoria-2025)).",
            "http://127.0.0.1:4000/solar/memoria-2025");
        var judge = new CannedJudge("{}");

        var marks = await Grading.MarksAsync(TheScenario(_grounded), recording, "key", judge);

        marks.ShouldHaveSingleItem().ShouldBe(new GradeMark("grounding", 5));
        judge.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARunThatNeverAnswered_ScoresNothingOnEveryDimension()
    {
        // A deadline is the model's own: an answer that did not arrive is the worst answer, and
        // leaving it out of the mean would rate a model on the runs it happened to finish.
        var recording = new Recording { TimedOut = true };
        var scenario = TheScenario(_facts, Grade.Judged("attribution", "Judge the sources."));
        var judge = new CannedJudge("""{"score": 9, "reason": "unused"}""");

        var marks = await Grading.MarksAsync(scenario, recording, "key", judge);

        marks.Select(m => (m.Dimension, m.Score)).ShouldBe([("accuracy", 0d), ("attribution", 0d)]);
        judge.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARunTheProviderRefused_IsNotGradedAtAll()
    {
        // Not the model's answer and not its fault: a 5xx scored as zero would make a provider's
        // bad afternoon a model's research score.
        var recording = new Recording { ProviderError = "502 Bad Gateway" };

        var marks = await Grading.MarksAsync(TheScenario(_facts), recording, "key", new CannedJudge("{}"));

        marks.ShouldBeEmpty();
    }

    [Fact]
    public async Task AJudgedDimension_IsTheJudgesScore_WithItsReason()
    {
        var recording = new Recording { Reply = "Fueron 1.577 según la crónica de 2025." };
        var judge = new CannedJudge("""{"score": 7, "reason": "one figure has no source"}""");

        var marks = await Grading.MarksAsync(
            TheScenario(Grade.Judged("attribution", "Judge whether each figure names its page.")),
            recording, "key", judge);

        var mark = marks.ShouldHaveSingleItem();
        mark.Dimension.ShouldBe("attribution");
        mark.Score.ShouldBe(7);
        mark.Reason.ShouldBe("one figure has no source");
        judge.Requests.ShouldHaveSingleItem().ShouldContain("Judge whether each figure names its page.");
        JsonDocument.Parse(judge.Requests[0]).RootElement.GetProperty("messages")[0]
            .GetProperty("content").GetString().ShouldNotBeNull().ShouldContain("\"score\": 0-10");
    }

    [Theory]
    [InlineData("""{"score": 14, "reason": "generous"}""")]
    [InlineData("""{"score": -1, "reason": "harsh"}""")]
    [InlineData("""{"pass": true, "reason": "the other question's answer"}""")]
    [InlineData("not a score")]
    public async Task AScoreOffTheScale_IsNoScore_AndTheDimensionIsLeftOut(string answer)
    {
        // Never a zero and never a clamp: a judge that did not answer the question asked has
        // said nothing about the model, and the mean must not move on it.
        var recording = new Recording { Reply = "Fueron 1.577." };
        var judge = new CannedJudge(answer);

        var marks = await Grading.MarksAsync(
            TheScenario(Grade.Judged("attribution", "Judge the sources.")), recording, "key", judge);

        marks.ShouldBeEmpty();
        judge.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheGradersUsage_LandsOnTheRecordingItGraded()
    {
        var recording = new Recording { Reply = "Fueron 1.577." };
        var judge = new CannedJudge("""{"score": 8, "reason": "fine"}""")
        {
            Usage = new { prompt_tokens = 900, completion_tokens = 20, cost = 0.003 }
        };

        await Grading.MarksAsync(
            TheScenario(Grade.Judged("attribution", "Judge the sources.")), recording, "key", judge);

        recording.Spend.ShouldBe(Spend.Of(Judge.Model, new ModelSpend(0.003m, 900, null, 20, Requests: 1)));
    }

    private sealed class CannedJudge(string answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public object? Usage { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { message = new { content = answer } } },
                        usage = Usage
                    }),
                    Encoding.UTF8, "application/json")
            };
        }
    }
}