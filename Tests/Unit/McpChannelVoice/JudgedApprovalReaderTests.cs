using Domain.DTOs.Metrics;
using Domain.Judgments;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Judgments;

namespace Tests.Unit.McpChannelVoice;

// The spec's rule (.scratch/jev-voice-approval/spec.md § Decisions) over the answers the probe
// measured. Jev alone acts when it is sure; a lean acts only beside a word list that leans the
// same way; anything else re-asks, and Jev absent is the word list alone, exactly as before.
public class JudgedApprovalReaderTests
{
    private const string Prompt = "¿Apruebas apagar las luces del salón y de la cocina? Di sí o no.";

    private static readonly ApprovalJudgmentSettings _shipped = new();

    private static JudgedApprovalReader Reader(IJudge judge, TimeProvider? time = null, ApprovalJudgmentSettings? settings = null) =>
        new(judge, settings ?? _shipped, time ?? TimeProvider.System);

    private static Task<ApprovalReading> ReadAsync(double approved, double declined, string answer) =>
        Reader(StubJudge.Nouls((JudgedApprovalReader.ApprovedQuestionId, approved), (JudgedApprovalReader.DeclinedQuestionId, declined)))
            .ReadAsync(Prompt, answer, CancellationToken.None);

    [Fact]
    public async Task ASureYes_Approves_OnTheJudgmentAlone()
    {
        var reading = await ReadAsync(0.95, 0.03, "adelante");

        reading.Response.ShouldBe(ApprovalResponse.Approved);
        reading.DecidedBy.ShouldBe(ApprovalDecider.Judgment);
        reading.Approved.ShouldBe(0.95);
        reading.Declined.ShouldBe(0.03);
    }

    [Fact]
    public async Task ASureNo_Declines_OnTheJudgmentAlone()
    {
        var reading = await ReadAsync(0.03, 0.94, "mejor no");

        reading.Response.ShouldBe(ApprovalResponse.Declined);
        reading.DecidedBy.ShouldBe(ApprovalDecider.Judgment);
    }

    // "sí, pero la de la cocina no": the word list hears a yes and no no; Jev hears a narrowing.
    [Fact]
    public async Task ANarrowedYes_IsAmbiguous_EvenThoughTheWordListApproves()
    {
        ApprovalGrammarParser.Parse("sí, pero la de la cocina").ShouldBe(ApprovalResponse.Approved);

        var reading = await ReadAsync(0.03, 0.69, "sí, pero la de la cocina");

        reading.Response.ShouldBe(ApprovalResponse.Ambiguous);
        reading.DecidedBy.ShouldBe(ApprovalDecider.Judgment);
    }

    [Fact]
    public async Task ALeaningYes_BesideAWordListYes_Approves_ByAgreement()
    {
        var reading = await ReadAsync(0.88, 0.02, "sí sí");

        reading.Response.ShouldBe(ApprovalResponse.Approved);
        reading.DecidedBy.ShouldBe(ApprovalDecider.Agreement);
        reading.Approved.ShouldBe(0.88);
    }

    [Fact]
    public async Task ALeaningYes_BesideAnAmbiguousWordList_IsAmbiguous()
    {
        var reading = await ReadAsync(0.88, 0.02, "venga, dale");

        reading.Response.ShouldBe(ApprovalResponse.Ambiguous);
    }

    [Fact]
    public async Task ALeaningYes_BesideAWordListNo_IsAmbiguous()
    {
        var reading = await ReadAsync(0.88, 0.02, "no");

        reading.Response.ShouldBe(ApprovalResponse.Ambiguous);
    }

    [Fact]
    public async Task ALeaningNo_BesideAWordListNo_Declines_ByAgreement()
    {
        var reading = await ReadAsync(0.06, 0.67, "no, never mind");

        reading.Response.ShouldBe(ApprovalResponse.Declined);
        reading.DecidedBy.ShouldBe(ApprovalDecider.Agreement);
    }

    // The one place a misjudgment would run a tool the word list refused. A sure yes acts alone
    // everywhere else; over a denial it re-asks instead, which costs one question on "no hay
    // problema, hazlo" and buys back the only path where Jev alone could act against a spoken no.
    [Theory]
    [InlineData("no")]
    [InlineData("no, cancel")]
    [InlineData("stop")]
    public async Task ASureYes_OverAWordListNo_IsAmbiguous(string answer)
    {
        ApprovalGrammarParser.Parse(answer).ShouldBe(ApprovalResponse.Declined);

        var reading = await ReadAsync(0.95, 0.03, answer);

        reading.Response.ShouldBe(ApprovalResponse.Ambiguous);
        reading.DecidedBy.ShouldBe(ApprovalDecider.Judgment);
    }

    // The mirror of it: a sure no over a word-list yes still declines, because refusing is the
    // safe direction and the word list is what a narrowed or sarcastic yes fools.
    [Fact]
    public async Task ASureNo_OverAWordListYes_Declines()
    {
        var reading = await ReadAsync(0.02, 0.96, "sí");

        reading.Response.ShouldBe(ApprovalResponse.Declined);
        reading.DecidedBy.ShouldBe(ApprovalDecider.Judgment);
    }

    [Theory]
    [InlineData("thank you.")]
    [InlineData("sí")]
    [InlineData("no")]
    public async Task Filler_IsAmbiguous_WhateverTheWordListSaysOfIt(string answer)
    {
        var reading = await ReadAsync(0.10, 0.16, answer);

        reading.Response.ShouldBe(ApprovalResponse.Ambiguous);
        reading.DecidedBy.ShouldBe(ApprovalDecider.Judgment);
    }

    [Theory]
    [InlineData(AbsenceReason.Unconfigured)]
    [InlineData(AbsenceReason.Deadline)]
    [InlineData(AbsenceReason.Error)]
    public async Task JevAbsent_TheWordListDecides_AndNoProbabilityIsReported(AbsenceReason reason)
    {
        var reading = await Reader(StubJudge.Absent(reason)).ReadAsync(Prompt, "sí, claro", CancellationToken.None);

        reading.Response.ShouldBe(ApprovalResponse.Approved);
        reading.DecidedBy.ShouldBe(ApprovalDecider.WordList);
        reading.Approved.ShouldBeNull();
        reading.Declined.ShouldBeNull();
    }

    [Fact]
    public async Task AJudgeMissingAQuestion_IsAbsent()
    {
        var reading = await Reader(StubJudge.Nouls((JudgedApprovalReader.ApprovedQuestionId, 0.97)))
            .ReadAsync(Prompt, "no", CancellationToken.None);

        reading.Response.ShouldBe(ApprovalResponse.Declined);
        reading.DecidedBy.ShouldBe(ApprovalDecider.WordList);
    }

    // A fake that ignores its token and answers after the deadline is not waited for: the person
    // is standing at the satellite, and the word list has an answer now.
    [Fact]
    public async Task AJudgeThatAnswersAfterTheDeadline_IsNotWaitedFor()
    {
        var time = new FakeTimeProvider();
        var never = new TaskCompletionSource<JudgmentOutcome>();
        var judge = new NeverAnsweringJudge(never.Task);

        var reading = Reader(judge, time).ReadAsync(Prompt, "sí", CancellationToken.None);
        await Eventually.Until(() => judge.Asked, "the reader asked the judge");
        reading.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromMilliseconds(_shipped.DeadlineMs));

        var read = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        read.Response.ShouldBe(ApprovalResponse.Approved);
        read.DecidedBy.ShouldBe(ApprovalDecider.WordList);
        read.Latency.ShouldBe(TimeSpan.FromMilliseconds(_shipped.DeadlineMs));
    }

    // The tool's own cancellation is the turn being torn down, not a late judge: no verdict at all,
    // rather than the word list approving on a turn nobody is waiting for.
    [Fact]
    public async Task TheCallersOwnCancellation_Propagates_InsteadOfFallingBackToTheWordList()
    {
        using var caller = new CancellationTokenSource();
        var judge = new NeverAnsweringJudge(new TaskCompletionSource<JudgmentOutcome>().Task);

        var reading = Reader(judge, new FakeTimeProvider()).ReadAsync(Prompt, "sí", caller.Token);
        await Eventually.Until(() => judge.Asked, "the reader asked the judge");
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // A token already cancelled when the reader is entered is the same tear-down. The judge answers
    // an immediate absence rather than throwing, so nothing on the await path raises for it and the
    // word list would have decided a turn nobody is waiting for.
    [Theory]
    [InlineData(AbsenceReason.Deadline)]
    [InlineData(AbsenceReason.Unconfigured)]
    public async Task ACancellationBeforeTheJudgeIsAsked_Propagates_InsteadOfFallingBackToTheWordList(AbsenceReason reason)
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => Reader(StubJudge.Absent(reason)).ReadAsync(Prompt, "sí", caller.Token));
    }

    [Fact]
    public async Task Disabled_NeverAsksTheJudge()
    {
        var judge = StubJudge.Nouls((JudgedApprovalReader.ApprovedQuestionId, 0.02), (JudgedApprovalReader.DeclinedQuestionId, 0.98));

        var reading = await Reader(judge, settings: _shipped with { Enabled = false })
            .ReadAsync(Prompt, "sí", CancellationToken.None);

        judge.Requests.ShouldBeEmpty();
        reading.Response.ShouldBe(ApprovalResponse.Approved);
        reading.DecidedBy.ShouldBe(ApprovalDecider.WordList);
    }

    // The state is the prompt and the answer, nothing else; the two questions are the ones the
    // probe measured, the narrowed yes named as the exception.
    [Fact]
    public async Task TheRequest_CarriesThePromptAndTheAnswer_AndTheTwoMeasuredQuestions()
    {
        var judge = StubJudge.Nouls((JudgedApprovalReader.ApprovedQuestionId, 0.95), (JudgedApprovalReader.DeclinedQuestionId, 0.03));

        await Reader(judge).ReadAsync(Prompt, "sí a todo", CancellationToken.None);

        var request = judge.Requests.ShouldHaveSingleItem();
        request.State.Count.ShouldBe(2);
        request.State["prompt"]!.GetValue<string>().ShouldBe(Prompt);
        request.State["answer"]!.GetValue<string>().ShouldBe("sí a todo");
        request.Questions.Keys.ShouldBe([JudgedApprovalReader.ApprovedQuestionId, JudgedApprovalReader.DeclinedQuestionId], ignoreOrder: true);
        request.Questions[JudgedApprovalReader.ApprovedQuestionId].ShouldBeOfType<NoulQuestion>()
            .Instructions.ShouldContain("A yes that changes or narrows what was asked is not permission");
        request.Questions[JudgedApprovalReader.DeclinedQuestionId].ShouldBeOfType<NoulQuestion>()
            .Instructions.ShouldContain("refuse it, or tell the assistant not to do it as asked");
    }

    // A deadline nobody can wait for is a misconfiguration, and it used to throw out of the
    // CancellationTokenSource constructor on every approval — after the person had already spoken.
    // The word list answers instead, which is what every other absence does.
    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task ADeadlineThatIsNotAWait_FallsBackToTheWordList_WithoutThrowing(int deadlineMs)
    {
        var judge = StubJudge.Nouls((JudgedApprovalReader.ApprovedQuestionId, 0.02), (JudgedApprovalReader.DeclinedQuestionId, 0.98));

        var reading = await Reader(judge, settings: _shipped with { DeadlineMs = deadlineMs })
            .ReadAsync(Prompt, "sí, claro", CancellationToken.None);

        reading.Response.ShouldBe(ApprovalResponse.Approved);
        reading.DecidedBy.ShouldBe(ApprovalDecider.WordList);
    }

    // Bars that cannot separate a yes from a no: every judged answer would clear the first arm and
    // approve, a spoken "no" included. Refuse the judgment rather than act on a bar nobody meant.
    [Theory]
    [InlineData(0.0, 0.1)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.4, 0.6)]
    public async Task BarsThatCannotSeparateAYesFromANo_FallBackToTheWordList(double sure, double counter)
    {
        var judge = StubJudge.Nouls((JudgedApprovalReader.ApprovedQuestionId, 0.02), (JudgedApprovalReader.DeclinedQuestionId, 0.98));

        var reading = await Reader(judge, settings: _shipped with { Sure = sure, Counter = counter })
            .ReadAsync(Prompt, "no", CancellationToken.None);

        reading.Response.ShouldBe(ApprovalResponse.Declined);
        reading.DecidedBy.ShouldBe(ApprovalDecider.WordList);
    }

    [Fact]
    public async Task AnEmptyAnswer_IsAmbiguous_WithoutAskingTheJudge()
    {
        var judge = StubJudge.Nouls((JudgedApprovalReader.ApprovedQuestionId, 0.95), (JudgedApprovalReader.DeclinedQuestionId, 0.03));

        var reading = await Reader(judge).ReadAsync(Prompt, "", CancellationToken.None);

        judge.Requests.ShouldBeEmpty();
        reading.Response.ShouldBe(ApprovalResponse.Ambiguous);
        reading.DecidedBy.ShouldBe(ApprovalDecider.WordList);
    }

    private sealed class NeverAnsweringJudge(Task<JudgmentOutcome> answer) : IJudge
    {
        public bool Asked { get; private set; }

        public Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline)
        {
            Asked = true;
            return answer;
        }
    }
}