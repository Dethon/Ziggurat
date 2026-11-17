using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Domain.Tools;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Unit.Domain.Monitor;

// The chat monitor driven through a fake channel, as the monitor tests drive it, but over the agent
// the deployment builds: McpAgent over the real ToolApprovalChatClient, its history kept by the real
// RedisChatMessageStore. Only the edges are stand-ins — the model is scripted, the tools are local
// functions, the store keeps its lines in memory — so a test sees a group's routing and the tool loop
// together, with no network, no model and no Docker. McpAgent rather than a bare ChatClientAgent
// because it dials nothing when its definition names no server, and it is where the turn's run options
// and the stored line are made in production.
internal sealed class ToolLoopRun : IAsyncDisposable
{
    public const string ConversationId = "conv-1";
    public const string AgentId = "jonas";
    private const string ChatChannel = "signalr";

    public static readonly Account Person = new("fedcba9876543210fedcba9876543210", "0123456789abcdef0123456789abcdef");

    private readonly InMemoryLines _lines = new();
    private readonly ChatThreadResolver _threads;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _monitor;

    // Every tool offered is whitelisted but those `refused`, which the channel turns down when asked —
    // the hook through which the approval client already ends a loop (FunctionInvocationContext.Terminate).
    // `sequential` runs a batch of calls one after another, as the library does with concurrency off.
    // `receipts` is what the channel answers each reply with, as a hub names the turn keys a stop handed back,
    // and `holdReply` holds a reply before the channel takes it. A `worker` is offered to the agent as the
    // run_subagent tool, and runs as production spawns one: an agent of its own over its own approval client,
    // sent the parent's context. `recall` is asked of every message as the turn is set up, as memory is.
    public ToolLoopRun(
        ScriptedModel model, IReadOnlyList<LocalTool> tools, IReadOnlyList<LocalTool>? refused = null,
        bool sequential = false, Worker? worker = null, Func<SendReplyParams, ReplyReceipt>? receipts = null,
        IMemoryRecallHook? recall = null, Func<SendReplyParams, Task>? holdReply = null,
        Func<IReadOnlyList<ChatMessage>, CancellationToken, Task>? beforeRun = null)
    {
        Channel = new FakeChannelConnection
        {
            ChannelId = ChatChannel, ApprovalAnswer = ToolApprovalResult.Rejected, ConversationIdToReturn = ConversationId,
            Receipts = receipts, HoldReply = holdReply
        };
        var agents = new ToolLoopAgents(model, tools, refused ?? [], sequential, worker, _lines, beforeRun);
        _threads = new ChatThreadResolver(_lines.Store);
        var monitor = new ChatMonitor(
            [Channel], agents, _threads, NoOpMetricsPublisher.Instance, recall,
            NullLogger<ChatMonitor>.Instance, MonitorTestMocks.Assertions, delegations: TestDelegations.Verifier());
        _monitor = Task.Run(() => monitor.Monitor(_stop.Token));
    }

    public sealed record Worker(ScriptedModel Model, IReadOnlyList<LocalTool> Tools)
    {
        public const string Id = "helper";

        public FunctionCallContent Call(string callId, string task) => new(
            callId, $"domain__subagents__{SubAgentRunTool.Name}",
            new Dictionary<string, object?> { ["subAgentId"] = Id, ["prompt"] = task });
    }

    // Somebody else of the household, whose own messages are signed for them.
    public static readonly Account OtherPerson = new(Person.TenantId, "abcdefabcdefabcdefabcdefabcdef12");

    public FakeChannelConnection Channel { get; }

    // The turns' replies, without the probes' refusals (AdmittedSoFarAsync).
    public IReadOnlyList<SendReplyParams> Replies =>
        [.. Channel.SentReplies.Where(r => r.TurnKey?.StartsWith(ProbePrefix, StringComparison.Ordinal) != true)];

    // A steer goes under the turn key the chat hub gives it, its queued id.
    public void Send(string text, bool steer = false, Account? from = null, string? turnKey = null)
    {
        var person = from ?? Person;
        Channel.WriteMessage(new ChannelMessage
        {
            ConversationId = ConversationId,
            Content = text,
            Sender = person == Person ? "Fran" : "Nuria",
            ChannelId = ChatChannel,
            AgentId = AgentId,
            Principal = new Principal(ChatChannel, person.UserId, person),
            Steer = steer,
            TurnKey = turnKey
        });
    }

    // Stop, as a channel's cancel reaches the monitor, naming the turn keys of the messages the stop
    // handed back to their senders.
    public void Stop(IReadOnlyList<string>? returned = null) => Channel.WriteUnsigned(new ChannelMessage
    {
        ConversationId = ConversationId,
        Content = "/cancel",
        Sender = ChannelProtocol.SystemSender,
        ChannelId = ChatChannel,
        AgentId = AgentId,
        ReturnedTurnKeys = returned
    });

    // A schedule Fran wrote, firing in this conversation: its delegation holds, so it acts for her, and it
    // is still a fire, not her turn.
    public void Fire(string prompt)
    {
        var schedule = new Schedule { Id = "s-1", AgentId = AgentId, Prompt = prompt, CronExpression = "0 8 * * *" };
        Channel.WriteUnsigned(new ChannelMessage
        {
            ConversationId = ConversationId,
            Content = prompt,
            Sender = ServiceSenders.Scheduler,
            ChannelId = ChatChannel,
            AgentId = AgentId,
            ReplyTo = [new ReplyTarget(ChannelIds.SignalR, null)],
            Origin = new MessageOrigin(MessageOriginKind.Schedule, schedule.Id),
            Delegation = TestDelegations.OfSchedule(Person, ChannelIds.SignalR, schedule)
        });
    }

    // Every message sent so far has been admitted, or refused: the group admits its messages one at a
    // time in the order they came, so once a message sent after them is refused, they are through. The
    // probe is that message, unsigned, and its refusal is left out of Replies.
    public async Task AdmittedSoFarAsync()
    {
        var probe = $"{ProbePrefix}{Interlocked.Increment(ref _probes)}";
        Channel.WriteUnsigned(new ChannelMessage
        {
            ConversationId = ConversationId,
            Content = "probe",
            Sender = "probe",
            ChannelId = ChatChannel,
            AgentId = AgentId,
            TurnKey = probe
        });
        await Eventually.Until(
            () => Channel.SentReplies.Any(r => r.TurnKey == probe && r.ContentType == ReplyContentType.StreamComplete),
            "the messages sent before the probe to be admitted");
    }

    private const string ProbePrefix = "probe-";

    // The conversation's group heard a stop: its context is let go as the cancel is taken, before anything
    // after it.
    public Task CancelHeardAsync() => Eventually.Until(
        () => !_threads.AgentKeys.Contains(new AgentKey(ConversationId, AgentId)), "the stop to reach the group");

    // A reply under none of the keys a test gives its messages: the turn the first message started.
    public static bool IsTheFirstTurns(SendReplyParams reply) =>
        reply.TurnKey is { } key && !key.StartsWith(ProbePrefix, StringComparison.Ordinal)
                                 && !key.StartsWith("steer-", StringComparison.Ordinal);
    private int _probes;

    // Ends the channel's stream; the monitor returns once every turn it holds has run to its end.
    public async Task FinishAsync()
    {
        Channel.Complete();
        await _monitor.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public IReadOnlyList<string> StoredLine() => Render(Line());

    // The conversation as the store keeps it, marks and all.
    public IReadOnlyList<ChatMessage> Line() => _lines.Line(new AgentKey(ConversationId, AgentId).ToString());

    public static IReadOnlyList<string> Render(IEnumerable<ChatMessage> line) =>
    [
        .. line.SelectMany(m => m.Contents.Select(c => c switch
        {
            TextContent text => $"{m.Role}: {text.Text}",
            FunctionCallContent call => $"{m.Role}: call {call.Name}#{call.CallId}",
            FunctionResultContent result => $"{m.Role}: result {result.CallId} = {result.Result}",
            _ => $"{m.Role}: {c.GetType().Name}"
        }))
    ];

    public async ValueTask DisposeAsync()
    {
        Channel.Complete();
        await _stop.CancelAsync();
        try
        {
            await _monitor.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private sealed class ToolLoopAgents(
        IChatClient model, IReadOnlyList<LocalTool> tools, IReadOnlyList<LocalTool> refused, bool sequential,
        Worker? worker, InMemoryLines lines, Func<IReadOnlyList<ChatMessage>, CancellationToken, Task>? beforeRun)
        : IAgentFactory
    {
        public Task<DisposableAgent> CreateAsync(
            AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler,
            Account? account = null, string? tenantId = null, CancellationToken ct = default, string? channelId = null)
        {
            var workers = worker is null
                ? []
                : new SubAgentToolFeature(new SubAgentRegistryOptions
                {
                    SubAgents = [new SubAgentDefinition { Id = Worker.Id, Name = Worker.Id, McpServerEndpoints = [] }]
                })
                    .GetTools(new FeatureConfig(
                        SubAgentFactory: definition => BuildWorker(definition, approvalHandler),
                        ConversationContextProvider: () => ConversationContextMeta.Current))
                    .ToList();
            var loop = new ToolApprovalChatClient(
                model, approvalHandler, agentKey.ConversationId,
                [.. tools.Select(t => t.Tool.Name).Concat(workers.Select(w => w.Name))])
            {
                AllowConcurrentInvocation = !sequential
            };
            var agent = new McpAgent(
                TestAgentSpec.Named(agentKey.ConversationId) with { AgentId = agentId ?? AgentId },
                loop, lines.Store, NoOpMetricsPublisher.Instance, TimeProvider.System,
                [.. tools.Concat(refused).Select(t => t.Tool), .. workers], []);
            return Task.FromResult<DisposableAgent>(beforeRun is null ? agent : new HeldRunAgent(agent, beforeRun));
        }

        // As production builds a worker: its own approval client over its own model, keeping no history.
        public DisposableAgent CreateSubAgent(
            SubAgentDefinition definition, IToolApprovalHandler approvalHandler, SpawnContext spawn) =>
            BuildWorker(definition, approvalHandler);

        private DisposableAgent BuildWorker(SubAgentDefinition definition, IToolApprovalHandler approvalHandler)
        {
            var own = worker ?? throw new NotSupportedException("this run offers no worker");
            var loop = new ToolApprovalChatClient(
                own.Model, approvalHandler, ConversationId, [.. own.Tools.Select(t => t.Tool.Name)]);
            return new McpAgent(
                TestAgentSpec.Named(ConversationId) with { AgentId = definition.Id, KeepsHistory = false },
                loop, new NullThreadStateStore(), NoOpMetricsPublisher.Instance, TimeProvider.System,
                [.. own.Tools.Select(t => t.Tool)], []);
        }
    }

    // The agent the deployment builds, with a wait in front of each run: the time between the conversation
    // group handing a turn over and the run reaching its history, where a stop leaves the history nothing
    // to decide.
    private sealed class HeldRunAgent(McpAgent inner, Func<IReadOnlyList<ChatMessage>, CancellationToken, Task> beforeRun)
        : DisposableAgent
    {
        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => inner.DisposeThreadSessionAsync(thread);

        public override Task WarmupSessionAsync(AgentSession thread, CancellationToken ct = default) =>
            inner.WarmupSessionAsync(thread, ct);

        public override Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(AgentSession thread, CancellationToken ct) =>
            inner.GetHistoryAsync(thread, ct);

        public override Task KeepUnansweredAsync(
            AgentSession thread, IReadOnlyList<ChatMessage> messages, string? failedWith, CancellationToken ct) =>
            inner.KeepUnansweredAsync(thread, messages, failedWith, ct);

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            inner.CreateSessionAsync(cancellationToken);

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            inner.SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) =>
            inner.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.RunAsync(messages, session, options, cancellationToken);

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ChatMessage> asked = [.. messages];
            await beforeRun(asked, cancellationToken);
            await foreach (var update in inner.RunStreamingAsync(asked, session, options, cancellationToken))
            {
                yield return update;
            }
        }
    }

    // The thread store as the history provider uses it, one list per key, read under a lock because
    // the turn appends from the monitor's thread while the test reads.
    private sealed class InMemoryLines
    {
        private readonly Dictionary<string, List<ChatMessage>> _lines = [];

        public InMemoryLines()
        {
            var store = new Mock<IThreadStateStore>();
            store.Setup(s => s.GetMessagesAsync(It.IsAny<string>()))
                .ReturnsAsync((string key) => Snapshot(key));
            store.Setup(s => s.GetMessageCountAsync(It.IsAny<string>()))
                .ReturnsAsync((string key) => (long)(Snapshot(key)?.Length ?? 0));
            store.Setup(s => s.GetTailMessagesAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync((string key, int count) => Snapshot(key)?.TakeLast(count).ToArray());
            store.Setup(s => s.AppendMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>()))
                .Returns((string key, IReadOnlyList<ChatMessage> messages) =>
                {
                    lock (_lines)
                    {
                        (_lines.TryGetValue(key, out var line) ? line : _lines[key] = []).AddRange(messages);
                    }

                    return Task.CompletedTask;
                });
            store.Setup(s => s.DropTailAsync(It.IsAny<string>(), It.IsAny<int>()))
                .Returns((string key, int count) =>
                {
                    lock (_lines)
                    {
                        if (_lines.TryGetValue(key, out var line))
                        {
                            line.RemoveRange(line.Count - count, count);
                        }
                    }

                    return Task.CompletedTask;
                });
            Store = store.Object;
        }

        public IThreadStateStore Store { get; }

        public IReadOnlyList<ChatMessage> Line(string key) => Snapshot(key) ?? [];

        private ChatMessage[]? Snapshot(string key)
        {
            lock (_lines)
            {
                return _lines.TryGetValue(key, out var line) ? [.. line] : null;
            }
        }
    }
}

// The model, one answer per call: each step is what that call says, and a call past the script ends the
// turn in a word. What each call was sent is kept, so a test reads what the model saw.
internal sealed class ScriptedModel(params IReadOnlyList<AIContent>[] steps) : IChatClient
{
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
    private readonly Dictionary<int, TaskCompletionSource> _holds = [];
    private readonly HashSet<int> _failing = [];

    // Call `step` fails, as a provider's error fails a turn.
    public void Fail(int step)
    {
        lock (_requests)
        {
            _failing.Add(step);
        }
    }

    // The answer to call `step` is held back once the call is made, until the action returned is run:
    // a turn still being written, with no tool boundary ahead of it.
    public Action Hold(int step)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_requests)
        {
            _holds[step] = held;
        }

        return () => held.TrySetResult();
    }

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("the agent streams every turn");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        // A request on a turn already stopped never reaches the provider, as a real client's would not.
        cancellationToken.ThrowIfCancellationRequested();
        int step;
        TaskCompletionSource? held;
        bool fails;
        lock (_requests)
        {
            step = _requests.Count;
            _requests.Add([.. messages]);
            held = _holds.GetValueOrDefault(step);
            fails = _failing.Contains(step);
        }

        if (fails)
        {
            throw new InvalidOperationException("the provider failed");
        }

        if (held is not null)
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }

        IReadOnlyList<AIContent> said = step < steps.Length ? steps[step] : [new TextContent("Done")];
        yield return new ChatResponseUpdate(ChatRole.Assistant, [.. said]) { MessageId = $"m{step}" };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

// A tool the agent offers as a domain tool. It touches nothing it shares with another, so the calls of
// one batch are in flight together; a held one starts when invoked and returns only once the test says.
internal sealed class LocalTool
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LocalTool(string name, string result, bool held, bool pastAStop = false)
    {
        if (!held)
        {
            _finish.SetResult();
        }

        Tool = FootprintDeclaration.Declared(
            AIFunctionFactory.Create(
                async (CancellationToken ct) =>
                {
                    _started.TrySetResult();
                    await _finish.Task.WaitAsync(pastAStop ? CancellationToken.None : ct);
                    return result;
                },
                name),
            CallFootprint.Nothing);
    }

    public static LocalTool Answering(string name, string result) => new(name, result, held: false);

    public static LocalTool Held(string name, string result) => new(name, result, held: true);

    // Held, and slow to hear a stop: it returns only once the test says, as a call already out on the wire
    // comes back when it does, so the stopped turn is still being torn down until then.
    public static LocalTool HeldPastAStop(string name, string result) => new(name, result, held: true, pastAStop: true);

    public AIFunction Tool { get; }

    // Bounded, so a call that never starts fails the test that expected it rather than hanging the run.
    public Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(60));

    public bool HasStarted => _started.Task.IsCompleted;

    public void Finish() => _finish.TrySetResult();

    public FunctionCallContent Call(string callId) => new(callId, Tool.Name, new Dictionary<string, object?>());
}