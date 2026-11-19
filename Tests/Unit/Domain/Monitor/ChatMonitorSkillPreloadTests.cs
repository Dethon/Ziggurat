using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
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

    // Most voice turns open a conversation of their own, so the wait above was on nearly every one.
    // The skills a group's warmup will find are the ones the same agent's last session found, so a
    // later group judges over those while its own warmup is still dialling.
    [Fact]
    public async Task ALaterGroupOfTheSameAgent_AsksTheJudgeWhileItsWarmupIsStillDialling()
    {
        var secondWarmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmups = 0;
        var agent = new FakeAiAgent
        {
            WarmupGate = () => Interlocked.Increment(ref warmups) == 1 ? Task.CompletedTask : secondWarmup.Task
        };
        agent.Skills = [_home];
        var judge = new DelayingJudge(new ArmedClock(), TimeSpan.Zero, Sure(_home.Name));
        var channel = new FakeChannelConnection();
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "enciende la luz"));
        var monitor = Monitor(agent, channel, recall: null, new SkillPreloader(judge, _settings, TimeProvider.System));

        var run = monitor.Monitor(CancellationToken.None);
        await Eventually.Until(() => agent.ReceivedMessages.Count == 1, "the first group's turn ran");
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(conversationId: "conv-2", content: "apaga la luz"));
        await Eventually.Until(() => judge.Asked.Count == 2, "the second group asks before its warmup ends");
        secondWarmup.SetResult();
        channel.Complete();
        await run;

        var preload = await SkillPreloadPending.TryTake(agent.ReceivedMessages.ToArray()[^1].Single()).ShouldNotBeNull();
        preload.Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
        preload.Skills.Select(s => s.Name).ShouldBe([_home.Name]);
    }

    // The servers a household reaches can change between two groups. A skill judged over the last
    // session's list that this session does not offer is not preloaded into it.
    [Fact]
    public async Task ASkillTheNewSessionDoesNotOffer_IsNotPreloaded()
    {
        var agent = new FakeAiAgent();
        agent.Skills = [_home];
        var judge = new DelayingJudge(new ArmedClock(), TimeSpan.Zero, Sure(_home.Name));
        var channel = new FakeChannelConnection();
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "enciende la luz"));
        var monitor = Monitor(agent, channel, recall: null, new SkillPreloader(judge, _settings, TimeProvider.System));

        var run = monitor.Monitor(CancellationToken.None);
        await Eventually.Until(() => agent.ReceivedMessages.Count == 1, "the first group's turn ran");
        agent.Skills = [];
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(conversationId: "conv-2", content: "apaga la luz"));
        channel.Complete();
        await run;

        var preload = await SkillPreloadPending.TryTake(agent.ReceivedMessages.ToArray()[^1].Single()).ShouldNotBeNull();
        preload.Skills.ShouldBeEmpty();
        preload.Reads.ShouldBeEmpty();
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

    // The judge's client sends nothing for a turn addressed to the local box, and bills the rest
    // to whoever asked, and it knows the turn only by what the request says: the group hands on
    // the model the message asked for and who sent it.
    [Fact]
    public async Task ThePreload_NamesTheModelTheTurnAskedFor_AndWhoAsked()
    {
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var judge = new CallerRecordingJudge();
        var channel = MonitorTestMocks.CreateChannel(
            messages:
            [
                MonitorTestMocks.CreateChannelMessage(content: "enciende la luz", sender: "fran", agentId: "jonas") with
                {
                    ConfigPatch = new AgentConfigPatch { Model = "lemonade/qwen3" }
                }
            ]);
        var monitor = Monitor(agent, channel, recall: null, new SkillPreloader(judge, _settings, TimeProvider.System));

        await monitor.Monitor(CancellationToken.None);

        var caller = judge.Callers.ShouldHaveSingleItem();
        caller.TurnModel.ShouldBe("lemonade/qwen3");
        caller.Sender.ShouldBe("fran");
        caller.AgentId.ShouldBe("jonas");
        caller.ConversationId.ShouldBe("conv-1");
    }

    // A household's preload is its call: asked on the key its agent pays with, and counted under it.
    [Fact]
    public async Task ThePreload_IsPaidByTheHouseholdTheAgentWasBuiltFor()
    {
        var payer = new ModelPayer("0123456789abcdef0123456789abcdef", new ModelKey("household-key"));
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        agent.PaidBy = payer;
        var judge = new DelayingJudge(new ArmedClock(), TimeSpan.Zero, Sure(_home.Name));
        var metrics = new RecordingMetricsPublisher();
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage(content: "enciende la luz"));
        var monitor = Monitor(agent, channel, recall: null, new SkillPreloader(judge, _settings, TimeProvider.System, metrics));

        await monitor.Monitor(CancellationToken.None);

        judge.Asked.ShouldHaveSingleItem().Caller.Payer.ShouldBe(payer);
        metrics.Published.OfType<SkillPreloadEvent>().ShouldHaveSingleItem().TenantId.ShouldBe(payer.TenantId);
    }

    private sealed class CallerRecordingJudge : IJudge
    {
        public List<JudgmentCaller> Callers { get; } = [];

        public Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline)
        {
            Callers.Add(request.Caller);
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

    // Steers that go in together are one turn of several messages: each is recalled for and extracted from
    // as it would have been alone, and the judge is asked once, for the last, which the skills provider
    // takes the pending answer from.
    [Fact]
    public async Task SteersThatGoInTogether_AreEachRecalledAndExtracted_AndPreloadedOnceForTheLast()
    {
        var agent = MonitorTestMocks.CreateAgent();
        agent.Skills = [_home];
        var judge = new DelayingJudge(new ArmedClock(), TimeSpan.Zero, Sure(_home.Name));
        var recall = new ExtractingRecallHook();
        var channel = new FakeChannelConnection
        {
            ChannelId = "test-channel",
            Receipts = reply => reply.IsComplete && reply.TurnKey is not ("steer-1" or "steer-2")
                ? ReplyReceipt.None with { SteeredTogether = ["steer-1", "steer-2"] }
                : ReplyReceipt.None
        };
        var message = MonitorTestMocks.CreateChannelMessage();
        channel.WriteMessage(message with { Content = "enciende la luz" });
        channel.WriteMessage(message with { Content = "la del salón", Steer = true, TurnKey = "steer-1" });
        channel.WriteMessage(message with { Content = "y la cocina", Steer = true, TurnKey = "steer-2" });
        channel.Complete();

        await Monitor(agent, channel, recall, new SkillPreloader(judge, _settings, TimeProvider.System)).Monitor(CancellationToken.None);

        agent.ReceivedMessages.TryDequeue(out _).ShouldBeTrue();
        agent.ReceivedMessages.TryDequeue(out var together).ShouldBeTrue();
        together!.Select(m => m.Text).ShouldBe(["la del salón", "y la cocina"]);
        together.ShouldAllBe(m => m.GetMemoryContext() != null);
        recall.Extracted.ShouldBe(["enciende la luz", "la del salón", "y la cocina"]);
        judge.Asked.Select(a => a.State["request"]!.GetValue<string>()).ShouldBe(["enciende la luz", "y la cocina"]);
        SkillPreloadPending.TryTake(together[0]).ShouldBeNull();
        (await SkillPreloadPending.TryTake(together[1]).ShouldNotBeNull()).Outcome.ShouldBe(SkillPreloadOutcome.Preloaded);
    }

    // Recalls for every message and leaves an extraction behind that says which message it was.
    private sealed class ExtractingRecallHook : IMemoryRecallHook
    {
        private readonly List<string> _extracted = [];

        public IReadOnlyList<string> Extracted
        {
            get
            {
                lock (_extracted)
                {
                    return [.. _extracted];
                }
            }
        }

        public Task<MemoryExtraction?> EnrichAsync(
            ChatMessage message, Account? account, string? conversationId, string? agentId, AgentSession thread, CancellationToken ct)
        {
            message.SetMemoryContext(new MemoryContext([], null));
            return Task.FromResult<MemoryExtraction?>(_ =>
            {
                lock (_extracted)
                {
                    _extracted.Add(message.Text);
                }

                return ValueTask.CompletedTask;
            });
        }
    }

    private static ChatMonitor Monitor(FakeAiAgent agent, FakeChannelConnection channel, IMemoryRecallHook? recall, ISkillPreloader preloader) =>
        new(
            [channel],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            recall,
            Mock.Of<ILogger<ChatMonitor>>(),
            TestAssertions.Verifier(),
            skillPreloader: preloader,
            skillsOffered: new global::Infrastructure.Agents.Skills.SkillsOfferedMemory());

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
        public Task<MemoryExtraction?> EnrichAsync(
            ChatMessage message, Account? account, string? conversationId, string? agentId, AgentSession thread, CancellationToken ct)
        {
            release.TrySetResult();
            return Task.FromResult<MemoryExtraction?>(null);
        }
    }

    private sealed class DelayingRecallHook(TimeProvider clock, TimeSpan delay) : IMemoryRecallHook
    {
        public async Task<MemoryExtraction?> EnrichAsync(
            ChatMessage message, Account? account, string? conversationId, string? agentId, AgentSession thread, CancellationToken ct)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, clock, ct);
            }

            message.SetMemoryContext(new MemoryContext([], null));
            return null;
        }
    }
}