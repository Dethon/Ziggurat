using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Judgments;
using Domain.Monitor;
using Domain.Prompts;
using Domain.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain.Skills;

namespace Tests.Unit.Domain.Monitor;

// The head start: a live turn asks the judge where it builds the user message, beside memory
// recall rather than after it, and the pending answer rides on the message. What is pinned is
// the overlap, that nothing about the preload can fail the build, and that a command asks nothing.
public class ChatMonitorSkillPreloadTests
{
    private static readonly PromptSkill _home = TestSkills.Home;

    private static readonly SkillPreloadSettings _settings = new() { DeadlineMs = 600 };

    [Fact]
    public async Task ARecallOf500msAndAJudgmentOf400ms_HaveTheMessageReadyAt500msNot900()
    {
        var clock = new ArmedClock();
        var recallSpan = TimeSpan.FromMilliseconds(500);
        var judgeSpan = TimeSpan.FromMilliseconds(400);
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var judge = new DelayingJudge(clock, judgeSpan, Sure(_home.Name));
        var recall = new DelayingRecallHook(clock, recallSpan);
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage(content: "enciende la luz"));
        var monitor = Monitor(agent, channel, recall, new SkillPreloader(judge, _settings, clock));

        var run = monitor.Monitor(CancellationToken.None);

        // Both waits are armed before either has ended: the judgment started beside recall, not
        // behind it.
        await clock.WaitUntilArmedAsync(judgeSpan);
        await clock.WaitUntilArmedAsync(recallSpan);
        clock.Advance(recallSpan);
        await run;

        var received = agent.ReceivedMessages.ShouldHaveSingleItem().ShouldHaveSingleItem();
        received.GetMemoryContext().ShouldNotBeNull();
        var preload = await SkillPreloadPending.TryTake(received).ShouldNotBeNull();
        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        preload.Skills.Select(s => s.Name).ShouldBe([_home.Name]);
        // Read off the clock after one advance, so it is the recall's span, not the judge's: what
        // it proves is that the judgment ended inside the recall's window.
        preload.Latency.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(recallSpan);
    }

    // A group's first turn builds its message while the warmup is still dialling the servers,
    // and the skills arrive with the servers: a judgment taken before then would be over nothing.
    [Fact]
    public async Task OnAGroupsFirstTurn_TheJudgmentWaitsForTheWarmup_SoItIsOverTheSessionsSkills()
    {
        var warmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new FakeAiAgent { WarmupGate = () => warmed.Task };
        agent.Skills = [_home];
        var judge = new DelayingJudge(new ArmedClock(), TimeSpan.Zero, Sure(_home.Name));
        // Recall runs inside the message build, so releasing the warmup from it puts the build
        // strictly before the warmup's end — the order the first turn of a live group has.
        var recall = new ReleasingRecallHook(warmed);
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage(content: "enciende la luz"));
        var monitor = Monitor(agent, channel, recall, new SkillPreloader(judge, _settings, TimeProvider.System));

        await monitor.Monitor(CancellationToken.None);

        var received = agent.ReceivedMessages.ShouldHaveSingleItem().ShouldHaveSingleItem();
        var preload = await SkillPreloadPending.TryTake(received).ShouldNotBeNull();
        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        preload.Skills.Select(s => s.Name).ShouldBe([_home.Name]);
    }

    [Fact]
    public async Task AJudgmentStillPendingWhenItsDeadlinePasses_RidesAsNoPreload()
    {
        var clock = new ArmedClock();
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var judge = new DelayingJudge(clock, TimeSpan.FromMilliseconds(5000), Sure(_home.Name));
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage(content: "enciende la luz"));
        var monitor = Monitor(agent, channel, recall: null, new SkillPreloader(judge, _settings, clock));

        var run = monitor.Monitor(CancellationToken.None);
        await Eventually.Until(() => agent.ReceivedMessages.Count == 1, "the turn does not wait for the judgment");
        await clock.AdvancePastAsync(TimeSpan.FromMilliseconds(_settings.DeadlineMs));
        await run;

        var received = agent.ReceivedMessages.Single().Single();
        var preload = await SkillPreloadPending.TryTake(received).ShouldNotBeNull();
        preload.Outcome.ShouldBe(SkillPreloadOutcome.Deadline);
        preload.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task AThrowingPreloader_NeverFailsTheBuild_AndRecallIsUnaffected()
    {
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var preloader = new Mock<ISkillPreloader>();
        preloader.Setup(p => p.PreloadAsync(It.IsAny<SkillPreloadRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("judge exploded"));
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage(content: "enciende la luz"));
        var monitor = Monitor(agent, channel, new DelayingRecallHook(new ArmedClock(), TimeSpan.Zero), preloader.Object);

        await monitor.Monitor(CancellationToken.None);

        var received = agent.ReceivedMessages.ShouldHaveSingleItem().ShouldHaveSingleItem();
        received.GetMemoryContext().ShouldNotBeNull();
        var preload = await SkillPreloadPending.TryTake(received).ShouldNotBeNull();
        preload.Outcome.ShouldBe(SkillPreloadOutcome.Error);
        preload.Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task ThePreload_IsJudgedOverTheSessionsSkillsAndAgainstItsPersistedHistory()
    {
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var judge = new DelayingJudge(new ArmedClock(), TimeSpan.Zero, Sure(_home.Name));
        var channel = MonitorTestMocks.CreateChannel(
            messages:
            [
                MonitorTestMocks.CreateChannelMessage(content: "enciende la luz"),
                MonitorTestMocks.CreateChannelMessage(content: "y ahora apágala")
            ]);
        var monitor = Monitor(agent, channel, recall: null, new SkillPreloader(judge, _settings, TimeProvider.System));

        await monitor.Monitor(CancellationToken.None);

        // The fake agent persists each turn as it runs it; the second turn's history is the
        // first turn, and the judge is asked over the skills the first turn did not load.
        judge.Asked.Count.ShouldBe(2);
        judge.Asked[1].State["request"]!.GetValue<string>().ShouldBe("y ahora apágala");
        judge.Asked[1].Questions["skill"].ShouldBeOfType<ChoiceQuestion>().Criteria.Keys.ShouldBe([_home.Name, "none"]);
    }

    // The Jev client reads the ambient turn model to see what the turn asked for, and outside a
    // tool call nobody enters one but the group: without this a live turn addressed to the local box
    // would reach the judge, the one place the client's rule could not see.
    [Fact]
    public async Task ThePreload_IsAskedAsTheTurn_SoTheJudgeSeesTheModelItAskedFor()
    {
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var judge = new CallerRecordingJudge();
        var channel = MonitorTestMocks.CreateChannel(
            messages:
            [
                MonitorTestMocks.CreateChannelMessage(content: "enciende la luz") with
                {
                    ConfigPatch = new AgentConfigPatch { Model = "lemonade/qwen3" }
                }
            ]);
        var monitor = Monitor(agent, channel, recall: null, new SkillPreloader(judge, _settings, TimeProvider.System));

        await monitor.Monitor(CancellationToken.None);

        judge.Models.ShouldBe(["lemonade/qwen3"]);
    }

    private sealed class CallerRecordingJudge : IJudge
    {
        public List<string?> Models { get; } = [];

        public Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline)
        {
            Models.Add(TurnModel.Current);
            return Task.FromResult<JudgmentOutcome>(new JudgmentOutcome.Absent(AbsenceReason.LocalTurn));
        }
    }

    // With the feature off — no key, or the flag down — the turn must cost exactly what it did
    // before the preload existed. The history read is a whole-thread Redis read the first model
    // call waits on, and asking the preloader is what says whether it is worth paying for.
    [Fact]
    public async Task WithThePreloadOff_TheThreadIsNotReadForIt()
    {
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var judge = new DelayingJudge(new ArmedClock(), TimeSpan.Zero, Sure(_home.Name));
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage(content: "enciende la luz"));
        var preloader = new SkillPreloader(judge, _settings with { Enabled = false }, TimeProvider.System);
        var monitor = Monitor(agent, channel, recall: null, preloader);

        await monitor.Monitor(CancellationToken.None);

        judge.Asked.ShouldBeEmpty();
        agent.HistoryReads.ShouldBe(0);
    }

    [Theory]
    [InlineData("/clear")]
    [InlineData("/cancel")]
    public async Task ACommand_StartsNoJudgment(string command)
    {
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var judge = new DelayingJudge(new ArmedClock(), TimeSpan.Zero, Sure(_home.Name));
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage(content: command));
        var monitor = Monitor(agent, channel, recall: null, new SkillPreloader(judge, _settings, TimeProvider.System));

        await monitor.Monitor(CancellationToken.None);

        judge.Asked.ShouldBeEmpty();
        agent.ReceivedMessages.ShouldBeEmpty();
    }

    private static ChatMonitor Monitor(FakeAiAgent agent, FakeChannelConnection channel, IMemoryRecallHook? recall, ISkillPreloader preloader) =>
        new(
            [channel],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            recall,
            Mock.Of<ILogger<ChatMonitor>>(),
            skillPreloader: preloader);

    private static JudgmentOutcome Sure(string skill) => JudgeAnswers.Sure(skill);

    private sealed class DelayingJudge(TimeProvider clock, TimeSpan delay, JudgmentOutcome answer) : IJudge
    {
        public List<JudgmentRequest> Asked { get; } = [];

        public async Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline)
        {
            Asked.Add(request);
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, clock, deadline);
                }
            }
            catch (OperationCanceledException)
            {
                return new JudgmentOutcome.Absent(AbsenceReason.Deadline);
            }

            return answer;
        }
    }

    private sealed class ReleasingRecallHook(TaskCompletionSource release) : IMemoryRecallHook
    {
        public Task EnrichAsync(
            ChatMessage message, string userId, string? conversationId, string? agentId, AgentSession thread, CancellationToken ct)
        {
            release.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class DelayingRecallHook(TimeProvider clock, TimeSpan delay) : IMemoryRecallHook
    {
        public async Task EnrichAsync(
            ChatMessage message, string userId, string? conversationId, string? agentId, AgentSession thread, CancellationToken ct)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, clock, ct);
            }

            message.SetMemoryContext(new MemoryContext([], null));
        }
    }
}