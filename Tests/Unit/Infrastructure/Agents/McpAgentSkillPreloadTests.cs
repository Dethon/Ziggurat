using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Judgments;
using Domain.Prompts;
using Domain.Skills;
using Infrastructure.Agents;
using Infrastructure.Metrics;
using Infrastructure.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;
using Tests.Integration.McpServers;
using Tests.Unit.Domain.Skills;
using Tests.Unit.Infrastructure.Helpers;

namespace Tests.Unit.Infrastructure.Agents;

// The preload end to end through a real agent: a judge sure of a skill puts its body into the
// first request as the pair a load leaves, after the user message, with everything else the
// model sees untouched; the pair is persisted, so the next turn neither asks about that skill
// nor inserts it twice; and every way the judge can fail to answer leaves the turn exactly as
// it is today.
public class McpAgentSkillPreloadTests
{
    private const string Home = "home-assistant";
    private const string Timers = "countdown-timers";

    private static readonly SkillText _homeText = new(Home, TestSkills.Home.Description, TestSkills.Home.Body);
    private static readonly SkillText _timersText = new(Timers, TestSkills.Timers.Description, TestSkills.Timers.Body);

    private static readonly SkillPreloadSettings _settings = new() { DeadlineMs = 600 };

    private static Task<RunningServer> StartAsync(params SkillText[] skills) =>
        InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FailingTools>()
            .AddSkills(skills));

    [Fact]
    public async Task AConfidentJudgment_PutsThePairAfterTheUserMessage_AndChangesNothingElse()
    {
        await using var server = await StartAsync(_homeText, _timersText);
        var judge = new ScriptedJudge(_ => Sure(Home));
        var (onClient, onCalls) = Capturing();
        var (offClient, offCalls) = Capturing();
        await using var on = Agent(onClient, server.Endpoint, Preloader(judge));
        await using var off = Agent(offClient, server.Endpoint, preloader: null);

        await on.RunAsync([new ChatMessage(ChatRole.User, "enciende la luz del salón")]);
        await off.RunAsync([new ChatMessage(ChatRole.User, "enciende la luz del salón")]);

        var withPreload = onCalls.ShouldHaveSingleItem();
        var without = offCalls.ShouldHaveSingleItem();

        withPreload.Messages.Select(m => m.Role.Value).ShouldBe(["user", "assistant", "tool"]);
        withPreload.Messages[0].Text.ShouldBe("enciende la luz del salón");
        var call = withPreload.Messages[1].Contents.OfType<FunctionCallContent>().ShouldHaveSingleItem();
        call.Name.ShouldBe(SkillLoadTool.Name);
        call.Arguments!.Values.Single()!.ToString().ShouldBe(Home);
        var result = withPreload.Messages[2].Contents.OfType<FunctionResultContent>().ShouldHaveSingleItem();
        result.CallId.ShouldBe(call.CallId);
        result.Result!.ToString().ShouldNotBeNull().ShouldContain("Call the house.");
        withPreload.Messages[1].Contents.OfType<TextReasoningContent>().ShouldHaveSingleItem().Text.ShouldContain(Home);

        without.Messages.Select(m => m.Role.Value).ShouldBe(["user"]);
        withPreload.Options!.Instructions.ShouldBe(without.Options!.Instructions);
        withPreload.Options.Tools!.Select(t => t.Name).ShouldBe(without.Options.Tools!.Select(t => t.Name));
        withPreload.Options.Tools!.Select(t => t.Name).ShouldContain(SkillLoadTool.Name);
    }

    // The live path: a conversation group starts the judgment where it builds the user message and
    // rides it on that message, and the skills provider takes it at insertion. The join is the
    // same ChatMessage instance surviving from the group through the agent and the framework into
    // the provider's context — input messages are filtered, not cloned — so nothing here may ask
    // the preloader itself. A framework that began stamping external messages by cloning them, as
    // it already does for history, would miss every take and judge a second time per turn with
    // every other test in this file still green.
    [Fact]
    public async Task AJudgmentAttachedToTheUserMessage_IsTakenByTheProvider_AndNoSecondOneIsAsked()
    {
        await using var server = await StartAsync(_homeText, _timersText);
        var judge = new ScriptedJudge(_ => Sure(Home));
        var (client, calls) = Capturing();
        var preloader = Preloader(judge);
        await using var agent = Agent(client, server.Endpoint, preloader);

        var message = new ChatMessage(ChatRole.User, "enciende la luz del salón");
        var request = new SkillPreloadRequest(message.Text, [TestSkills.Home, TestSkills.Timers], []);
        SkillPreloadPending.Attach(message, preloader.PreloadAsync(request, CancellationToken.None));

        await agent.RunAsync([message]);

        // The attached judgment is the one that landed, and the provider asked for no other.
        judge.Asked.ShouldHaveSingleItem();
        var sent = calls.ShouldHaveSingleItem();
        sent.Messages.Select(m => m.Role.Value).ShouldBe(["user", "assistant", "tool"]);
        sent.Messages[1].Contents.OfType<FunctionCallContent>().ShouldHaveSingleItem()
            .Arguments!.Values.Single()!.ToString().ShouldBe(Home);

        // Taken once: a second turn on the same message must not find the judgment again.
        SkillPreloadPending.TryTake(message).ShouldBeNull();
    }

    [Fact]
    public async Task ThePair_IsPersisted_AndTheNextTurnAsksAboutOneSkillFewerAndInsertsNothingTwice()
    {
        await using var server = await StartAsync(_homeText, _timersText);
        var judge = new ScriptedJudge(_ => Sure(Home));
        var (client, calls) = Capturing();
        var (store, persisted) = InMemoryStore();
        await using var agent = Agent(client, server.Endpoint, Preloader(judge), store);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync([new ChatMessage(ChatRole.User, "enciende la luz")], session);
        await agent.RunAsync([new ChatMessage(ChatRole.User, "y ahora apágala")], session);

        var thread = persisted.Values.ShouldHaveSingleItem();
        thread.Select(m => m.Role.Value).ShouldBe(
            ["user", "assistant", "tool", "assistant", "user", "assistant"]);
        SkillLoadTool.LoadedIn(thread).ShouldBe([Home]);

        judge.Asked.Count.ShouldBe(2);
        var second = judge.Asked[1].Questions["skill"].ShouldBeOfType<ChoiceQuestion>();
        second.Criteria.Keys.ShouldBe([Timers, "none"]);

        var secondRequest = calls[1].Messages;
        secondRequest.SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Count(c => c.Name == SkillLoadTool.Name).ShouldBe(1);
        secondRequest.Last().Role.Value.ShouldBe("user");
    }

    [Fact]
    public async Task TwoSkills_ArriveAsOneAssistantMessageWithTwoCallsAndTheirTwoResults()
    {
        await using var server = await StartAsync(_homeText, _timersText);
        var judge = new ScriptedJudge(_ => Answered(Home, 0.97, (Home, 0.96), (Timers, 0.93)));
        var (client, calls) = Capturing();
        await using var agent = Agent(client, server.Endpoint, Preloader(judge));

        await agent.RunAsync([new ChatMessage(ChatRole.User, "apaga las luces y pon un temporizador")]);

        var request = calls.ShouldHaveSingleItem().Messages;
        request.Select(m => m.Role.Value).ShouldBe(["user", "assistant", "tool"]);
        var loads = request[1].Contents.OfType<FunctionCallContent>().ToList();
        loads.Select(c => c.Arguments!.Values.Single()!.ToString()).ShouldBe([Home, Timers]);
        var results = request[2].Contents.OfType<FunctionResultContent>().ToList();
        results.Select(r => r.CallId).ShouldBe(loads.Select(c => c.CallId));
        results[0].Result!.ToString().ShouldNotBeNull().ShouldContain("Call the house.");
        results[1].Result!.ToString().ShouldNotBeNull().ShouldContain("Write timer.json.");
    }

    [Fact]
    public async Task AJudgmentSlowerThanTheDeadline_InsertsNothing_AndItsLateAnswerNeverReachesALaterTurn()
    {
        await using var server = await StartAsync(_homeText, _timersText);
        var clock = new ArmedClock();
        var late = new TaskCompletionSource<JudgmentOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var judge = new ScriptedJudge(async (_, ct) =>
        {
            // Answers only once the deadline has cancelled it, and then confidently: a late
            // answer is the one that must be discarded.
            var cancelled = new TaskCompletionSource();
            await using var registration = ct.Register(() => cancelled.TrySetResult());
            await cancelled.Task;
            return await late.Task;
        });
        var (client, calls) = Capturing();
        await using var agent = Agent(client, server.Endpoint, new SkillPreloader(judge, _settings, clock));

        var turn = agent.RunAsync([new ChatMessage(ChatRole.User, "enciende la luz")]);
        await clock.AdvancePastAsync(TimeSpan.FromMilliseconds(_settings.DeadlineMs));
        late.SetResult(Sure(Home));
        var response = await turn;

        response.Text.ShouldBe("ok");
        calls.ShouldHaveSingleItem().Messages.Select(m => m.Role.Value).ShouldBe(["user"]);

        judge.Answer = _ => Answered("none", 0.99);
        await agent.RunAsync([new ChatMessage(ChatRole.User, "gracias")]);
        calls[1].Messages.Select(m => m.Role.Value).ShouldBe(["user"]);
    }

    [Fact]
    public async Task ATurnPatchedToALemonadeModel_IsAskedAsOne_SoTheClientSendsNothing()
    {
        // The rule is the Jev client's (TypeSafeJudgeTests): it reads the ambient turn model and
        // sends nothing. What this path owes it is that ambient, from a request whose only mark
        // is its own patch — a run with no conversation context, as the eval's are.
        await using var server = await StartAsync(_homeText);
        string? seen = null;
        var judge = new ScriptedJudge(_ =>
        {
            seen = TurnModel.Current;
            return new JudgmentOutcome.Absent(AbsenceReason.LocalTurn);
        });
        var (client, calls) = Capturing();
        var spec = TestAgentSpec.Default with
        {
            McpServerEndpoints = [McpServerEndpoint.Configured(server.Endpoint)],
            PatchableModels = new FixedPatchableModelSource(["lemonade/qwen3"])
        };
        await using var agent = new McpAgent(
            spec, client, new Mock<IThreadStateStore>().Object, NoOpMetricsPublisher.Instance,
            TimeProvider.System, [], [], skillPreloader: Preloader(judge));
        var message = new ChatMessage(ChatRole.User, "enciende la luz");
        message.SetConfigPatch(new AgentConfigPatch { Model = "lemonade/qwen3" });

        await agent.RunAsync([message]);

        seen.ShouldBe("lemonade/qwen3");
        calls.ShouldHaveSingleItem().Messages.Select(m => m.Role.Value).ShouldBe(["user"]);
    }

    // A worker's user message is its delegation prompt, and the same provider judges it.
    [Fact]
    public async Task AWorkerRun_IsJudgedOnItsDelegationPrompt()
    {
        await using var server = await StartAsync(_homeText, _timersText);
        var judge = new ScriptedJudge(_ => Sure(Timers));
        var (client, calls) = Capturing();
        await using var worker = Agent(client, server.Endpoint, Preloader(judge));

        await worker.RunAsync([new ChatMessage(ChatRole.User, "Set a ten minute timer in the kitchen and report back.")]);

        judge.Asked.ShouldHaveSingleItem().State["request"]!.GetValue<string>()
            .ShouldBe("Set a ten minute timer in the kitchen and report back.");
        calls.ShouldHaveSingleItem().Messages[1].Contents.OfType<FunctionCallContent>()
            .ShouldHaveSingleItem().Arguments!.Values.Single()!.ToString().ShouldBe(Timers);
    }

    [Fact]
    public async Task AfterAnAbstention_TheLoadToolIsStillOfferedAndAModelMadeLoadWorksAsToday()
    {
        await using var server = await StartAsync(_homeText, _timersText);
        var judge = new ScriptedJudge(_ => Answered(Home, 0.6, (Home, 0.7)));
        var (client, calls) = Capturing(
            ToolApprovalResponseFactory.CreateToolCallResponse(
                SkillLoadTool.Name, "call-1", new Dictionary<string, object?> { [SkillLoadTool.SkillNameParameter] = Home }),
            new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done")]) { FinishReason = ChatFinishReason.Stop });
        await using var agent = Agent(client, server.Endpoint, Preloader(judge));

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "enciende la luz")]);

        response.Text.ShouldBe("Done");
        calls.Count.ShouldBe(2);
        calls[0].Messages.Select(m => m.Role.Value).ShouldBe(["user"]);
        calls[0].Options!.Tools!.Select(t => t.Name).ShouldContain(SkillLoadTool.Name);
        calls[1].Messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .ShouldHaveSingleItem().Result!.ToString().ShouldNotBeNull().ShouldContain("Call the house.");
    }

    [Theory]
    [InlineData(AbsenceReason.Error)]
    [InlineData(AbsenceReason.Unconfigured)]
    public async Task AJudgeThatAnswersAbsence_LeavesTheTurnAsToday(AbsenceReason reason)
    {
        await using var server = await StartAsync(_homeText);
        var judge = new ScriptedJudge(_ => new JudgmentOutcome.Absent(reason));
        var (client, calls) = Capturing();
        await using var agent = Agent(client, server.Endpoint, Preloader(judge));

        var response = await agent.RunAsync([new ChatMessage(ChatRole.User, "enciende la luz")]);

        response.Text.ShouldBe("ok");
        calls.ShouldHaveSingleItem().Messages.Select(m => m.Role.Value).ShouldBe(["user"]);
    }

    // Tool-call counts keep meaning "the model called it": the pair is written into the
    // conversation, never invoked, so nothing about it reaches the tool metrics.
    [Fact]
    public async Task APreload_PublishesNoToolCallEvent()
    {
        await using var server = await StartAsync(_homeText);
        var published = new RecordingMetricsPublisher();
        var judge = new ScriptedJudge(_ => Sure(Home));
        var (client, calls) = Capturing();
        await using var agent = new McpAgent(
            TestAgentSpec.Default with { McpServerEndpoints = [McpServerEndpoint.Configured(server.Endpoint)] },
            client, new Mock<IThreadStateStore>().Object, published, TimeProvider.System, [], [],
            skillPreloader: new SkillPreloader(judge, _settings, TimeProvider.System, published));

        await agent.RunAsync([new ChatMessage(ChatRole.User, "enciende la luz")]);

        calls.ShouldHaveSingleItem().Messages.Select(m => m.Role.Value).ShouldBe(["user", "assistant", "tool"]);
        published.Published.OfType<ToolCallEvent>().ShouldBeEmpty();
        published.Published.OfType<SkillPreloadEvent>().ShouldHaveSingleItem().Outcome.ShouldBe(SkillPreloadOutcomes.Preloaded);
    }

    private static McpAgent Agent(
        IChatClient chatClient, string endpoint, ISkillPreloader? preloader, IThreadStateStore? store = null) =>
        new(
            TestAgentSpec.Default with { McpServerEndpoints = [McpServerEndpoint.Configured(endpoint)] },
            chatClient,
            store ?? new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            [],
            skillPreloader: preloader);

    private static SkillPreloader Preloader(IJudge judge) => new(judge, _settings, TimeProvider.System);

    private static JudgmentOutcome Sure(string skill) => JudgeAnswers.Sure(skill);

    private static JudgmentOutcome Answered(string choice, double confidence, params (string Skill, double P)[] needs) =>
        JudgeAnswers.Answered(choice, confidence, needs);

    // A thread store that keeps what the history provider appends, keyed as it keys it.
    private static (IThreadStateStore Store, Dictionary<string, List<ChatMessage>> Persisted) InMemoryStore()
    {
        var persisted = new Dictionary<string, List<ChatMessage>>();
        var store = new Mock<IThreadStateStore>();
        store.Setup(s => s.GetMessagesAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => persisted.GetValueOrDefault(key)?.ToArray());
        store.Setup(s => s.GetMessageCountAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => (long)(persisted.GetValueOrDefault(key)?.Count ?? 0));
        store.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => persisted.ContainsKey(key));
        store.Setup(s => s.AppendMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Returns((string key, IReadOnlyList<ChatMessage> messages) =>
            {
                if (!persisted.TryGetValue(key, out var list))
                {
                    persisted[key] = list = [];
                }

                list.AddRange(messages);
                return Task.CompletedTask;
            });
        return (store.Object, persisted);
    }

    private sealed record Captured(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

    // Records every request the model receives and answers from a script, "ok" once it runs out.
    private static (IChatClient Client, List<Captured> Calls) Capturing(params ChatResponse[] responses)
    {
        var calls = new List<Captured>();
        return (new CapturingChatClient(calls, responses), calls);
    }

    private sealed class CapturingChatClient(List<Captured> calls, ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            calls.Add(new Captured([.. messages], options));
            return Task.FromResult(Next());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            calls.Add(new Captured([.. messages], options));
            return Next().ToChatResponseUpdates().ToAsyncEnumerable();
        }

        private ChatResponse Next() => _responses.Count > 0
            ? _responses.Dequeue()
            : new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]) { FinishReason = ChatFinishReason.Stop };

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}