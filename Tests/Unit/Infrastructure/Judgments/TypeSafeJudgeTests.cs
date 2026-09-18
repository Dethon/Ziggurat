using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Judgments;
using Infrastructure.Judgments;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Tests.Unit.Infrastructure.Judgments;

// The one client that speaks to TypeSafe, pinned at the wire: the request it sends for a choice
// and two nouls, the answers it hands back, and the rule that no failure of the service ever
// becomes a failure of the caller — every one of them is an absence with a reason.
public class TypeSafeJudgeTests
{
    private static readonly TypeSafeOptions _options = new()
    {
        ApiUrl = "https://typesafe.test/v1/",
        ApiKey = "ts-key",
        Model = "jev-1.13.0"
    };

    private const string Answered =
        """
        {
          "model": "jev-1.13.0",
          "answers": {
            "skill": { "type": "choice", "choice": "timers", "confidence": 0.93,
                       "probabilities": { "none": 0.02, "home": 0.05, "timers": 0.93 } },
            "needs_timers": { "type": "noul", "noul": 0.97 },
            "needs_home": { "type": "noul", "noul": 0.04 }
          },
          "usage": { "input_tokens": 372, "output_tokens": 59 }
        }
        """;

    private static JudgmentRequest AChoiceAndTwoNouls() => new(
        new JsonObject { ["request"] = "pon un temporizador de ocho minutos" },
        new Dictionary<string, JudgmentQuestion>
        {
            ["skill"] = new ChoiceQuestion(
                "Which one skill does the request need first?",
                new Dictionary<string, string>
                {
                    ["timers"] = "Countdowns and alarms.",
                    ["home"] = "Lights and climate.",
                    ["none"] = "None of these."
                }),
            ["needs_timers"] = new NoulQuestion("Does the request need this skill? Skill: Countdowns and alarms."),
            ["needs_home"] = new NoulQuestion("Does the request need this skill? Skill: Lights and climate.")
        });

    [Fact]
    public async Task Judge_AChoiceAndTwoNouls_PostsOneRequestOfTheDocumentedShape()
    {
        var handler = new ScriptedHandler(_ => Ok(Answered));
        var judge = Judge(handler);

        await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        var sent = handler.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.Uri.ShouldBe("https://typesafe.test/v1/systemone");
        sent.Authorization.ShouldBe("Bearer ts-key");

        var body = JsonNode.Parse(sent.Body)!.AsObject();
        body["state"]!["request"]!.GetValue<string>().ShouldBe("pon un temporizador de ocho minutos");
        body["model"]!.GetValue<string>().ShouldBe("jev-1.13.0");
        var questions = body["questions"]!.AsObject();
        questions.Count.ShouldBe(3);
        questions["skill"]!["type"]!.GetValue<string>().ShouldBe("choice");
        questions["skill"]!["instructions"]!.GetValue<string>().ShouldBe("Which one skill does the request need first?");
        questions["skill"]!["criteria"]!.AsObject().Select(c => c.Key).ShouldBe(["timers", "home", "none"]);
        questions["skill"]!["criteria"]!["none"]!.GetValue<string>().ShouldBe("None of these.");
        questions["needs_timers"]!["type"]!.GetValue<string>().ShouldBe("noul");
        questions["needs_timers"]!["criteria"].ShouldBeNull();
        questions["needs_home"]!["instructions"]!.GetValue<string>().ShouldEndWith("Lights and climate.");
    }

    [Fact]
    public async Task Judge_AnAnsweredCall_ExposesEachAnswerTypeAndTheUsage()
    {
        var judge = Judge(new ScriptedHandler(_ => Ok(Answered)));

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        var judgment = outcome.ShouldBeOfType<JudgmentOutcome.Answered>().Judgment;
        judgment.Model.ShouldBe("jev-1.13.0");
        judgment.Usage.InputTokens.ShouldBe(372);
        judgment.Usage.OutputTokens.ShouldBe(59);

        var choice = judgment.Answers["skill"].ShouldBeOfType<ChoiceAnswer>();
        choice.Choice.ShouldBe("timers");
        choice.Confidence.ShouldBe(0.93);
        choice.Probabilities["home"].ShouldBe(0.05);
        choice.Probabilities["none"].ShouldBe(0.02);

        judgment.Answers["needs_timers"].ShouldBeOfType<NoulAnswer>().Probability.ShouldBe(0.97);
        judgment.Answers["needs_home"].ShouldBeOfType<NoulAnswer>().Probability.ShouldBe(0.04);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData((HttpStatusCode)529)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Judge_AStatusThatMeansAbsence_AnswersErrorAndThrowsNothing(HttpStatusCode status)
    {
        var judge = Judge(new ScriptedHandler(_ => new HttpResponseMessage(status)));

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Error);
    }

    [Fact]
    public async Task Judge_AConnectionFailure_AnswersErrorAndThrowsNothing()
    {
        var judge = Judge(new ScriptedHandler(_ =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))));

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Error);
    }

    [Fact]
    public async Task Judge_ABodyThatIsNotAnAnswer_AnswersErrorAndThrowsNothing()
    {
        var judge = Judge(new ScriptedHandler(_ => Ok("<html>gateway</html>")));

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Error);
    }

    // A 200 whose content type the JSON reader refuses throws a different exception than a body
    // that is not JSON, and it must not be the one that escapes.
    [Fact]
    public async Task Judge_ASuccessWithAContentTypeThatIsNotJson_AnswersErrorAndThrowsNothing()
    {
        var judge = Judge(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>gateway</html>", System.Text.Encoding.UTF8, "text/html")
        }));

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Error);
    }

    [Fact]
    public async Task Judge_AKeyRejectionThenAModelRejection_LogsEachSettingOnce()
    {
        var logger = new RecordingLogger();
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.UnprocessableEntity]);
        var judge = Judge(new ScriptedHandler(_ => new HttpResponseMessage(statuses.Dequeue())), logger);

        await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);
        await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);
        await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        errors.Count.ShouldBe(2);
        errors[0].ShouldContain("typeSafe:apiKey");
        errors[1].ShouldContain("typeSafe:model");
    }

    [Fact]
    public async Task Judge_ADeadlineAlreadyPassed_AnswersDeadlineAndThrowsNothing()
    {
        var handler = new ScriptedHandler(_ => Ok(Answered));
        var judge = Judge(handler);
        using var deadline = new CancellationTokenSource();
        await deadline.CancelAsync();

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), deadline.Token);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Deadline);
    }

    [Fact]
    public async Task Judge_ADeadlinePassingMidCall_AnswersDeadlineAndThrowsNothing()
    {
        using var deadline = new CancellationTokenSource();
        var handler = new ScriptedHandler(async ct =>
        {
            await deadline.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return Ok(Answered);
        });
        var judge = Judge(handler);

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), deadline.Token);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Deadline);
    }

    // The client's own timeout, with the caller's token untouched: still a deadline, because the
    // reason the answer is missing is time.
    [Fact]
    public async Task Judge_TheClientTimingOut_AnswersDeadlineAndThrowsNothing()
    {
        var handler = new ScriptedHandler(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Ok(Answered);
        });
        var judge = Judge(handler, timeout: TimeSpan.FromMilliseconds(50));

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Deadline);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "typeSafe:apiKey")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "typeSafe:model")]
    public async Task Judge_AConfigurationRejection_AnswersErrorAndLogsTheSettingOnce(HttpStatusCode status, string setting)
    {
        var logger = new RecordingLogger();
        var judge = Judge(new ScriptedHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error\":\"rejected\"}")
        }), logger);

        var first = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);
        var second = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        first.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Error);
        second.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Error);
        var error = logger.Entries.Where(e => e.Level == LogLevel.Error).ShouldHaveSingleItem();
        error.Message.ShouldContain(setting);
    }

    [Fact]
    public async Task Create_AnEmptyKey_MakesNoHttpCallAndAnswersUnconfigured()
    {
        var handler = new ScriptedHandler(_ => Ok(Answered));
        var judge = TypeSafeJudge.Create(
            new HttpClient(handler), _options with { ApiKey = "" }, new RecordingLogger());

        var outcome = await judge.JudgeAsync(AChoiceAndTwoNouls(), CancellationToken.None);

        outcome.ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Unconfigured);
        handler.Requests.ShouldBeEmpty();
    }

    private static IJudge Judge(ScriptedHandler handler, ILogger? logger = null, TimeSpan? timeout = null)
    {
        var httpClient = new HttpClient(handler);
        if (timeout is { } t)
        {
            httpClient.Timeout = t;
        }

        return TypeSafeJudge.Create(httpClient, _options, logger ?? new RecordingLogger());
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed record SentRequest(HttpMethod Method, string Uri, string? Authorization, string Body);

    private sealed class ScriptedHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public ScriptedHandler(Func<CancellationToken, HttpResponseMessage> respond)
            : this(ct => Task.FromResult(respond(ct)))
        {
        }

        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest(
                request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return await respond(cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}