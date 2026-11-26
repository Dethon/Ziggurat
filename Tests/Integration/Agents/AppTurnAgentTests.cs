using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Memory;
using Domain.Monitor;
using Domain.Prompts;
using Domain.Security;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Domain.Tools.Memory;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Memory;
using Infrastructure.Utils;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;
using Tests.Unit.Domain;
using Tests.Unit.Domain.Skills;

namespace Tests.Integration.Agents;

// A built app's ask, as the agent runs it: a message on the deployment's `apps` channel carrying the app
// turn, handed to the monitor with the household's agents built by the real factory against the fake
// model, a real server dialled, the real recall hook and a skill judge. The same person's ordinary turn
// with the same agent, beside it, sends everything the ask must not — the persona, every kind of tool,
// the skills, a recall block — so each absence below is one that could have been there.
public sealed class AppTurnAgentTests : IAsyncLifetime
{
    private const string Household = "fedcba9876543210fedcba9876543210";
    private const string Instructions = "You are the carb estimator of this app. Answer with the grams of carbohydrate.";
    private const string AskText = "How many carbs in a slice of bread?";
    private const string ChatText = "What did I eat yesterday?";
    private const string Remembered = "Prefers answers in grams";

    private static readonly Account _viewer = new(Household, "0123456789abcdef0123456789abcdef");

    private static readonly AppTurn _app = new() { AppId = "a1b2c3d4e5f6", Instructions = Instructions };

    private FakeOpenRouter _model = null!;
    private SugaryServer _server = null!;
    private readonly KitCalls _kitCalls = new();
    private RunningServer _kit = null!;

    private readonly List<MetricEvent> _published = [];
    private readonly MemoryExtractionQueue _extractions = new();
    private readonly Mock<IEmbeddingService> _embeddings = new();
    private const string Answered = "Nothing was done: nobody here may approve that.";

    // What the person answers a question on their own chat.
    private ToolApprovalResult _personAnswers = ToolApprovalResult.Approved;

    private readonly ScriptedJudge _judge = new(_ => new JudgmentOutcome.Absent(AbsenceReason.Error));

    public async Task InitializeAsync()
    {
        _model = await FakeOpenRouter.StartAsync();
        _server = await SugaryServer.StartAsync();
        _kit = await InMemoryMcpServer.StartAsync(services =>
        {
            services.AddSingleton(_kitCalls);
            services.AddSingleton(new MachineMount(_kitCalls));
            services.AddToolServer(new KitSettings())
                .WithTools<KitTools>()
                .AddFileSystemTools<MachineMount>()
                .AddFileSystemResource<MachineMount>();
        });
    }

    public async Task DisposeAsync()
    {
        await _kit.DisposeAsync();
        await _server.DisposeAsync();
        await _model.DisposeAsync();
    }

    [Fact]
    public async Task AnAsk_IsSentTheAppsInstructions_WithNoPersona_NoTools_NoSkills_AndNoMemory()
    {
        var (apps, chat) = await RunAsync(Ask(), Chat());

        var ordinary = RequestFor(ChatText);
        ordinary.SystemPrompt.ShouldNotBeNull().ShouldContain("You are Jonas.");
        ordinary.SystemPrompt.ShouldContain("ALWAYS-SIGN-AS-JONAS");
        ordinary.SystemPrompt.ShouldContain("## Memory System");
        ordinary.SystemPrompt.ShouldContain("## Subagent Delegation");
        ordinary.SystemPrompt.ShouldContain("`/sugary`");
        ordinary.SystemPrompt.ShouldContain("<skill>");
        ordinary.SystemPrompt.ShouldContain("## User Context");
        ordinary.SystemPrompt.ShouldContain("VOICE RULES");
        ordinary.Body.ToJsonString().ShouldContain("Offline household servers: vault.");
        ordinary.ToolNames.ShouldContain(SkillLoadTool.Name);
        ordinary.ToolNames.ShouldContain(n => n.StartsWith("domain__filesystem__", StringComparison.Ordinal));
        ordinary.ToolNames.ShouldContain(n => n.StartsWith("domain__memory__", StringComparison.Ordinal));
        ordinary.ToolNames.ShouldContain(n => n.StartsWith("domain__subagents__", StringComparison.Ordinal));
        ordinary.Body.ToJsonString().ShouldContain(Remembered);

        var ask = RequestFor(AskText);
        var prompt = ask.SystemPrompt.ShouldNotBeNull();
        prompt.ShouldStartWith(CoreDirectivePrompt.Instructions);
        prompt.ShouldContain(Instructions);
        prompt.ShouldContain("Today is");
        prompt.ShouldNotContain("You are Jonas");
        prompt.ShouldNotContain("ALWAYS-SIGN-AS-JONAS");
        prompt.ShouldNotContain("## Memory System");
        prompt.ShouldNotContain("## Subagent Delegation");
        prompt.ShouldNotContain("## User Context");
        prompt.ShouldNotContain("VOICE RULES");
        prompt.ShouldNotContain("`/sugary`");
        prompt.ShouldNotContain("<skill>");
        prompt.ShouldNotContain("load_skill");
        (ask.Body["tools"] as JsonArray ?? []).ShouldBeEmpty($"tools went out: {ask.Body["tools"]?.ToJsonString()}");
        ask.Body.ToJsonString().ShouldNotContain(Remembered);
        ask.Body.ToJsonString().ShouldNotContain("Offline household servers");

        // Answered on its own channel, and nowhere else.
        string.Concat(apps.SentReplies.Where(r => r.TurnKey == "ask-turn").Select(r => r.Content)).ShouldNotBeEmpty();
        apps.SentReplies.Last().ContentType.ShouldBe(ReplyContentType.StreamComplete);
        chat.CreatedConversations.ShouldBeEmpty();

        // Recalled for and extracted from the ordinary turn alone; no judge asked which skill the ask needs.
        _embeddings.Verify(e => e.GenerateEmbeddingAsync(It.Is<string>(s => s.Contains(AskText)), It.IsAny<CancellationToken>()), Times.Never);
        _extractions.Complete();
        (await _extractions.ReadAllAsync(CancellationToken.None).ToListAsync())
            .ShouldHaveSingleItem().ConversationId.ShouldBe("chat-1");
        _judge.Asked.Select(r => r.Caller.ConversationId).ShouldBe(["chat-1"]);
    }

    // An ask declaring none of the agent's servers dials none: a server of the agent's that cannot be
    // reached costs it nothing.
    [Fact]
    public async Task AnAsk_DialsNoneOfTheAgentsServers_SoOneThatIsDownCostsItNothing()
    {
        var (apps, _) = await RunAsync(Jonas() with { McpServerEndpoints = [DeadEnd.Url("/mcp")] }, Ask());

        apps.SentReplies.ShouldNotContain(r => r.ContentType == ReplyContentType.Error);
        string.Concat(apps.SentReplies.Select(r => r.Content)).ShouldNotBeEmpty();
        RequestFor(AskText).SystemPrompt.ShouldNotBeNull().ShouldContain(Instructions);
    }

    // What the ask cost is the household's, under the person who asked, on the apps channel and naming the app.
    [Fact]
    public async Task AnAsksUsage_IsCountedOnTheAppsChannel_UnderTheViewer_NamingTheApp()
    {
        await RunAsync(Ask());

        var usage = Published.OfType<TokenUsageEvent>().ShouldHaveSingleItem();
        usage.Channel.ShouldBe(AppChannels.Apps);
        usage.AppId.ShouldBe(_app.AppId);
        usage.UserId.ShouldBe(_viewer.UserId);
        usage.TenantId.ShouldBe(Household);
        usage.AgentId.ShouldBe("jonas");
    }

    [Fact]
    public async Task AnOrdinaryTurnsUsage_NamesItsChannel_AndNoApp()
    {
        await RunAsync(Chat());

        var usage = Published.OfType<TokenUsageEvent>().ShouldHaveSingleItem();
        usage.Channel.ShouldBe(ChannelIds.SignalR);
        usage.AppId.ShouldBeNull();
    }

    // The servers an ask may use are the building agent's that the app declared: their tools, mounts,
    // skills and prompts, and nothing else of the agent — no workers, no memory, no undeclared server. A
    // declared server the agent does not dial is simply absent. The servers are each ask's own: a later ask
    // on the same conversation declaring fewer is offered fewer, and one declaring none is offered nothing.
    [Fact]
    public async Task EachAsk_IsOfferedOnlyItsDeclaredServers_AndALaterAskWithFewerIsOfferedFewer()
    {
        var (apps, _) = await RunAsync(
            Jonas() with { McpServerEndpoints = [_server.Endpoint, KitName, "vault"] },
            Chat(),
            Ask("Both servers", [KitName, "sugary", "vault", "not-the-agents"], "ask-a"),
            Ask("The kit alone", [KitName], "ask-b"),
            Ask("Nothing at all", [], "ask-c"));

        var ordinary = RequestFor(ChatText);
        ordinary.SystemPrompt.ShouldNotBeNull().ShouldContain("`/sugary`");
        ordinary.ToolNames.ShouldContain($"mcp__{Kit}__whoami");
        ordinary.ToolNames.ShouldContain(n => n.StartsWith("domain__memory__", StringComparison.Ordinal));
        ordinary.ToolNames.ShouldContain(n => n.StartsWith("domain__subagents__", StringComparison.Ordinal));

        var both = RequestFor("Both servers");
        both.ToolNames.ShouldContain($"mcp__{Kit}__whoami");
        both.ToolNames.ShouldContain(n => n.StartsWith("domain__filesystem__", StringComparison.Ordinal));
        both.ToolNames.ShouldContain(SkillLoadTool.Name);
        both.ToolNames.ShouldNotContain(n => n.StartsWith("domain__memory__", StringComparison.Ordinal));
        both.ToolNames.ShouldNotContain(n => n.StartsWith("domain__subagents__", StringComparison.Ordinal));
        var bothPrompt = both.SystemPrompt.ShouldNotBeNull();
        bothPrompt.ShouldContain("`/machine`");
        bothPrompt.ShouldContain("`/sugary`");
        bothPrompt.ShouldContain("<skill>");
        bothPrompt.ShouldContain(Instructions);
        bothPrompt.ShouldNotContain("You are Jonas");
        bothPrompt.ShouldNotContain("## Memory System");
        bothPrompt.ShouldNotContain("## Subagent Delegation");
        // A declared household server that is offline is named so, as on any turn.
        both.Body.ToJsonString().ShouldContain("Offline household servers: vault.");

        var kit = RequestFor("The kit alone");
        kit.ToolNames.ShouldContain($"mcp__{Kit}__whoami");
        kit.ToolNames.ShouldContain(n => n.StartsWith("domain__filesystem__", StringComparison.Ordinal));
        kit.ToolNames.ShouldNotContain(SkillLoadTool.Name);
        var kitPrompt = kit.SystemPrompt.ShouldNotBeNull();
        kitPrompt.ShouldContain("`/machine`");
        kitPrompt.ShouldNotContain("`/sugary`");
        kitPrompt.ShouldNotContain("<skill>");
        kit.Body.ToJsonString().ShouldNotContain("Offline household servers");

        var none = RequestFor("Nothing at all");
        (none.Body["tools"] as JsonArray ?? []).ShouldBeEmpty($"tools went out: {none.Body["tools"]?.ToJsonString()}");
        none.SystemPrompt.ShouldNotBeNull().ShouldNotContain("`/machine`");

        apps.SentReplies.ShouldNotContain(r => r.ContentType == ReplyContentType.Error);
    }

    // Nothing an ask calls may be put to anybody: a call that would be asked — by the whitelist, the exec
    // screen (Jev's verdict missing on a person's own machine) or because it acts as the person on a site —
    // is refused at once, never sent to the anchor and never offered elsewhere. The same three calls on the
    // person's own turn are each asked.
    [Fact]
    public async Task ACallThatWouldBeAsked_IsRefusedAtOnce_AndPutToNobody()
    {
        ScriptTheAskedCalls();
        var (_, ownChat) = await RunAsync(Jonas() with { McpServerEndpoints = [KitName] }, Chat());

        ownChat.AskedApprovals.SelectMany(a => a.Requests).Select(r => r.ToolName)
            .ShouldBe([$"mcp__{Kit}__unlock_door", $"mcp__{Kit}__browse", "domain__filesystem__exec"]);
        _kitCalls.Ran.ShouldBe(["unlock_door", "browse", "exec"]);
        _kitCalls.Clear();

        ScriptTheAskedCalls(answeringEach: true);
        var (apps, chat) = await RunAsync(
            Jonas() with { McpServerEndpoints = [KitName] },
            Ask("Unlock the door", [KitName], "ask-a"),
            Ask("Read my bank page", [KitName], "ask-b"),
            Ask("Run it on my laptop", [KitName], "ask-c"));

        _kitCalls.Ran.ShouldBeEmpty();
        // Each scripted call was offered and made: a call to a tool the ask lacked would end it in an error.
        apps.SentReplies.ShouldNotContain(r => r.ContentType == ReplyContentType.Error);
        apps.AskedApprovals.ShouldBeEmpty();
        apps.OfferedApprovals.ShouldBeEmpty();
        apps.NotifyAutoApprovedCalls.ShouldBeEmpty();
        chat.AskedApprovals.ShouldBeEmpty();
        chat.OfferedApprovals.ShouldBeEmpty();
        // Each ask went on past its refused call to an answer, and the exec screen was asked of the third.
        apps.SentReplies.Where(r => r.ContentType == ReplyContentType.StreamComplete).Select(r => r.TurnKey)
            .ShouldBe(["ask-a", "ask-b", "ask-c"]);
        AnswerOf(apps, "ask-a").ShouldBe(Answered);
        AnswerOf(apps, "ask-b").ShouldBe(Answered);
        AnswerOf(apps, "ask-c").ShouldBe(Answered);
        _judge.Asked.ShouldContain(r => r.Caller.ConversationId == "ask-1");
    }

    // A call that never asks runs, as the person viewing the app: the call token the server verified names
    // them. Nothing tells the apps channel it was approved, since nobody there is watching a conversation.
    [Fact]
    public async Task ACallThatNeverAsks_Runs_ActingAsTheViewer()
    {
        _model.Script(FakeOpenRouter.CallingToolNamed("whoami", "{}"));
        var (apps, _) = await RunAsync(Jonas() with { McpServerEndpoints = [KitName] }, Ask("Who am I?", [KitName], "ask-a"));

        _kitCalls.Ran.ShouldBe(["whoami"]);
        _kitCalls.Callers.ShouldBe([_viewer.UserId]);
        _model.Requests.Last().ToolOutputs.ShouldHaveSingleItem().ShouldContain(_viewer.UserId);
        apps.NotifyAutoApprovedCalls.ShouldBeEmpty();
        apps.AskedApprovals.ShouldBeEmpty();
    }

    // An app declares a server by the name it is configured under — a shared server by its `sharedServers`
    // key, a household's own server by its own name — never by how its tools are qualified, which spells
    // the deployment's hosts and ports and would break a published app when they move.
    [Fact]
    public async Task AnAsk_DeclaresServersByTheirConfiguredNames_NeverByTheNamesTheirToolsCarry()
    {
        var (apps, _) = await RunAsync(
            Jonas() with { McpServerEndpoints = [_server.Endpoint, KitName] },
            Ask("By their names", ["sugary", KitName], "ask-a"),
            Ask("By what their tools carry", [Sugary, Kit], "ask-b"));

        var named = RequestFor("By their names");
        named.SystemPrompt.ShouldNotBeNull().ShouldContain("`/sugary`");
        named.ToolNames.ShouldContain($"mcp__{Kit}__whoami");

        var qualified = RequestFor("By what their tools carry");
        (qualified.Body["tools"] as JsonArray ?? []).ShouldBeEmpty($"tools went out: {qualified.Body["tools"]?.ToJsonString()}");
        qualified.SystemPrompt.ShouldNotBeNull().ShouldNotContain("`/sugary`");

        apps.SentReplies.ShouldNotContain(r => r.ContentType == ReplyContentType.Error);
    }

    // A refused call is the tool's result, and the ask goes on to its answer: the app waits on words, and
    // the model may say it could not, or answer without it. The result says what happened — nobody could be
    // asked — never that a person turned it down and more input is coming, which would steer a model to stop.
    [Fact]
    public async Task ARefusedCall_IsTheToolsResult_SayingNobodyCouldBeAsked_AndTheAskGoesOnToItsAnswer()
    {
        _model.Script(FakeOpenRouter.CallingToolNamed("unlock_door", "{}"), _ => FakeOpenRouter.Saying(Answered));
        var (apps, _) = await RunAsync(Jonas() with { McpServerEndpoints = [KitName] }, Ask("Unlock the door", [KitName], "ask-a"));

        _kitCalls.Ran.ShouldBeEmpty();
        _model.Requests.Last().ToolOutputs.ShouldHaveSingleItem().ShouldBe(
            $"Tool execution was refused: mcp__{Kit}__unlock_door needs a person's approval, which an app's request "
            + "cannot ask for, so it was not run. Carry on without it: answer with what you have, or say it could not be done.");
        AnswerOf(apps, "ask-a").ShouldBe(Answered);
        apps.SentReplies.ShouldNotContain(r => r.ContentType == ReplyContentType.Error);
    }

    // A person who turns a call down in their own chat ends their turn there, as ever: the model is not asked
    // again. The words that call leaves are pinned where the client is (ToolApprovalChatClientExecScreenTests).
    [Fact]
    public async Task APersonsNo_EndsTheirTurn_WithNoFurtherRequest()
    {
        _personAnswers = ToolApprovalResult.Rejected;
        _model.Script(FakeOpenRouter.CallingToolNamed("unlock_door", "{}"), _ => FakeOpenRouter.Saying(Answered));
        var (_, chat) = await RunAsync(Jonas() with { McpServerEndpoints = [KitName] }, Chat());

        chat.AskedApprovals.ShouldHaveSingleItem();
        _kitCalls.Ran.ShouldBeEmpty();
        _model.Requests.Count(r => r.Path.Contains("responses", StringComparison.Ordinal))
            .ShouldBe(1, "the model was asked again after the person said no");
    }

    // A model that keeps calling what cannot be asked would loop to the invocation limit within the ask's
    // timeout, the exec screen asking Jev each time: the third refused call of an ask ends it, as a person's
    // no ends theirs, and the model is not asked again. Two of the three are one site's as-person calls, the
    // second refused by the first's answer rather than asked, and counted all the same.
    [Fact]
    public async Task AnAsksThirdRefusedCall_EndsIt_WithNoFurtherRequest()
    {
        _model.Script(
            FakeOpenRouter.CallingToolNamed("browse", """{"url":"https://bank.example.com/account","asMe":true}"""),
            FakeOpenRouter.CallingToolNamed("browse", """{"url":"https://bank.example.com/cards","asMe":true}"""),
            FakeOpenRouter.CallingToolNamed("unlock_door", "{}"),
            _ => FakeOpenRouter.Saying(Answered));
        var (apps, _) = await RunAsync(Jonas() with { McpServerEndpoints = [KitName] }, Ask("Do it all", [KitName], "ask-a"));

        _kitCalls.Ran.ShouldBeEmpty();
        _model.Requests.Count(r => r.Body["input"]!.ToJsonString().Contains("Do it all", StringComparison.Ordinal))
            .ShouldBe(3, "the model was asked again after the ask's third refused call");
        apps.SentReplies.Where(r => r.ContentType == ReplyContentType.StreamComplete).Select(r => r.TurnKey)
            .ShouldBe(["ask-a"]);
        AnswerOf(apps, "ask-a").ShouldBeEmpty();
        apps.SentReplies.ShouldNotContain(r => r.ContentType == ReplyContentType.Error);
        apps.AskedApprovals.ShouldBeEmpty();
    }

    // The three calls, one after another in one turn; or, for three asks, each followed by the ask's answer.
    private void ScriptTheAskedCalls(bool answeringEach = false)
    {
        Func<JsonObject, string>[] calls =
        [
            FakeOpenRouter.CallingToolNamed("unlock_door", "{}"),
            FakeOpenRouter.CallingToolNamed("browse", """{"url":"https://bank.example.com/account","asMe":true}"""),
            FakeOpenRouter.CallingToolNamed("exec", """{"path":"/machine","command":"ls"}""")
        ];
        _model.Script(answeringEach ? [.. calls.SelectMany(c => new[] { c, _ => FakeOpenRouter.Saying(Answered) })] : calls);
    }

    private static string AnswerOf(FakeChannelConnection apps, string turnKey) =>
        string.Concat(apps.SentReplies.Where(r => r.TurnKey == turnKey && r.ContentType == ReplyContentType.Text).Select(r => r.Content));

    // The kit is a household server of the household's own, which an app declares by its own name, as it
    // declares a shared server by its `sharedServers` key.
    private const string KitName = "kit";

    // The name the agent's tools carry the kit under, which is its address's, not the name it is declared by.
    private string Kit => McpServerEndpoint.Configured(_kit.Endpoint).Server!.Qualifier;

    private string Sugary => McpServerEndpoint.Configured(_server.Endpoint).Server!.Qualifier;

    private FakeOpenRouter.Request RequestFor(string text) =>
        _model.Requests.Where(r => r.Path.Contains("responses", StringComparison.Ordinal))
            .Single(r => r.Body["input"]!.ToJsonString().Contains(text, StringComparison.Ordinal));

    private IReadOnlyList<MetricEvent> Published
    {
        get
        {
            lock (_published)
            {
                return [.. _published];
            }
        }
    }

    private static ChannelMessage Ask(string text = AskText, IReadOnlyList<string>? servers = null, string turnKey = "ask-turn") => new()
    {
        ConversationId = "ask-1",
        Content = text,
        Sender = "app",
        ChannelId = AppChannels.Apps,
        AgentId = "jonas",
        TurnKey = turnKey,
        Principal = new Principal(AppChannels.Apps, _viewer.UserId, _viewer),
        Assertion = TestAssertions.Of(_viewer, AppChannels.Apps),
        AppTurn = _app with { Servers = servers ?? [] },
        // Asked for, and never made: an app's conversation is no topic.
        ReviewCopy = new ReviewCopy { Title = "Ask" }
    };

    private static ChannelMessage Chat() => new()
    {
        ConversationId = "chat-1",
        Content = ChatText,
        Sender = "Fran",
        ChannelId = ChannelIds.SignalR,
        AgentId = "jonas",
        Principal = new Principal(ChannelIds.SignalR, _viewer.UserId, _viewer),
        Assertion = TestAssertions.Of(_viewer, ChannelIds.SignalR)
    };

    private Task<(FakeChannelConnection Apps, FakeChannelConnection Chat)> RunAsync(params ChannelMessage[] messages) =>
        RunAsync(Jonas(), messages);

    private async Task<(FakeChannelConnection Apps, FakeChannelConnection Chat)> RunAsync(
        AgentDefinition jonas, params ChannelMessage[] messages)
    {
        var apps = new FakeChannelConnection { ChannelId = AppChannels.Apps };
        var chat = new FakeChannelConnection
        {
            ChannelId = ChannelIds.SignalR, HostsReviewCopies = true, ConversationIdToReturn = "topic-1",
            ApprovalAnswer = _personAnswers
        };
        foreach (var message in messages)
        {
            (message.ChannelId == AppChannels.Apps ? apps : chat).WriteUnsigned(message);
        }

        apps.Complete();
        chat.Complete();

        var households = new AgentHouseholds();
        households.Add(Household, [jonas], new FeaturesSection { Apps = true }, ownServers: ["vault", KitName]);
        var definitions = new AgentDefinitionProvider(
            new HouseholdAgentSource(households, new SharedServers { Sugary = _server.Endpoint }), new CustomAgentRegistry());

        var monitor = new ChatMonitor(
            [apps, chat],
            Factory(definitions),
            new ChatThreadResolver(),
            Metrics(),
            RecallHook(definitions),
            NullLogger<ChatMonitor>.Instance,
            TestAssertions.Verifier(),
            skillPreloader: new SkillPreloader(_judge, new SkillPreloadSettings { DeadlineMs = 600 }, TimeProvider.System),
            agentDefinitions: definitions,
            standing: new HouseholdAccountStanding(households));

        await monitor.Monitor(CancellationToken.None);
        return (apps, chat);
    }

    // The household's own Jonas, every kind of tool and section an agent can carry: a server with a mount
    // and skills, a household server it set up and is offline, the file tools, workers and memory, a
    // persona, its owner's instructions and a channel's section.
    private AgentDefinition Jonas() => new()
    {
        Id = "jonas",
        Name = "Jonas",
        Description = "The household's assistant.",
        Model = "deepseek/deepseek-v4.1-flash-1017",
        McpServerEndpoints = [_server.Endpoint, "vault"],
        PromptSections = [VoicePrompt.Name],
        EnabledFeatures = ["filesystem", "subagents", "memory"],
        CustomInstructions = "ALWAYS-SIGN-AS-JONAS",
        WhitelistPatterns = ["domain__filesystem*", "mcp__*__whoami", "mcp__*__browse"]
    };

    private MultiAgentFactory Factory(IAgentDefinitionProvider definitions)
    {
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(Mock.Of<IThreadStateStore>())
            .AddSingleton(Mock.Of<IHouseholdKeyReports>())
            .AddSingleton<IOwnServers>(new InProcessOwnServers(new Dictionary<string, string> { [KitName] = _kit.Endpoint }))
            .AddSingleton(McpTestSecret.Credentials)
            .AddSingleton(McpTestSecret.CallTokens)
            // The real screen over the judge, which never answers here: a command headed for a person's own
            // machine is then asked about.
            .AddSingleton<IExecScreen>(new ExecScreen(_judge, new ExecScreenSettings(), TimeProvider.System))
            .BuildServiceProvider();
        return new MultiAgentFactory(
            services, definitions, new OpenRouterConfig { ApiUrl = _model.ApiUrl, ApiKey = "deployment-key" },
            new DomainToolRegistry(
            [
                new SubAgentToolFeature(new SubAgentRegistryOptions
                {
                    SubAgents = [new SubAgentDefinition { Id = "worker", Name = "Worker", McpServerEndpoints = [] }]
                }),
                new MemoryToolFeature(Mock.Of<IMemoryStore>(), _embeddings.Object)
            ]),
            Metrics());
    }

    // The real recall hook over a person who has something to remember: it recalls for an ordinary turn,
    // so finding nothing of it in the ask's request is the gate, not an empty store.
    private MemoryRecallHook RecallHook(IAgentDefinitionProvider definitions)
    {
        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.HasAnyMemoriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<float[]?>(),
                It.IsAny<IEnumerable<MemoryCategory>?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MemorySearchResult(new MemoryEntry
                {
                    Id = "mem_1", UserId = _viewer.UserId, Category = MemoryCategory.Preference, Content = Remembered,
                    Importance = 0.9, Confidence = 0.9, CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
                }, 0.9)
            ]);
        _embeddings.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[8]);
        return new MemoryRecallHook(
            store.Object, _embeddings.Object, Mock.Of<IThreadStateStore>(), _extractions, Metrics(), definitions,
            NullLogger<MemoryRecallHook>.Instance, new MemoryRecallOptions());
    }

    private IMetricsPublisher Metrics()
    {
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                lock (_published)
                {
                    _published.Add(e);
                }
            });
        return metrics.Object;
    }

    private sealed record KitSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    // What the kit server ran, and as whom: only what its call token verified.
    private sealed class KitCalls
    {
        private readonly Lock _gate = new();
        private readonly List<string> _ran = [];
        private readonly List<string?> _callers = [];

        public IReadOnlyList<string> Ran
        {
            get
            {
                lock (_gate)
                {
                    return [.. _ran];
                }
            }
        }

        public IReadOnlyList<string?> Callers
        {
            get
            {
                lock (_gate)
                {
                    return [.. _callers];
                }
            }
        }

        public void Record(string tool, string? caller = null)
        {
            lock (_gate)
            {
                _ran.Add(tool);
                _callers.Add(caller);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _ran.Clear();
                _callers.Clear();
            }
        }
    }

    // A household server with one tool of each kind an ask may meet: one that never asks, one the whitelist
    // leaves to the person, and one that can act as the person on a site.
    [McpServerToolType]
    private sealed class KitTools(KitCalls calls)
    {
        [McpServerTool(Name = "whoami")]
        [Description("Says who is calling.")]
        public string WhoAmI(RequestContext<CallToolRequestParams> call)
        {
            calls.Record("whoami", call.Identity?.Subject);
            return $"You are {call.Identity?.Subject}.";
        }

        [McpServerTool(Name = "unlock_door")]
        [Description("Unlocks the front door.")]
        public string UnlockDoor()
        {
            calls.Record("unlock_door");
            return "Unlocked.";
        }

        [McpServerTool(Name = "browse")]
        [Description("Opens a web page, as the person when asMe is true.")]
        public string Browse(string url, bool asMe = false)
        {
            calls.Record("browse");
            return "A page.";
        }
    }

    // A person's own machine: a shell that reaches the host.
    private sealed class MachineMount(KitCalls calls) : FileSystemBackendBase
    {
        public override string FilesystemName => "machine";

        public override string DescribeMount => "Somebody's own computer.";

        public override ShellReach? ShellReach => global::Domain.DTOs.ShellReach.Host;

        public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
        {
            calls.Record("exec");
            return Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(new FsExecResult
            {
                Stdout = "ran", Stderr = "", ExitCode = 0, Truncated = false, TimedOut = false, DurationMs = 1, Cwd = ""
            }));
        }
    }
}