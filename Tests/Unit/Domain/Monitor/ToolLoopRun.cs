using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Domain.Tools;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
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
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _monitor;

    // Every tool offered is whitelisted but those `refused`, which the channel turns down when asked —
    // the hook through which the approval client already ends a loop (FunctionInvocationContext.Terminate).
    // `sequential` runs a batch of calls one after another, as the library does with concurrency off.
    public ToolLoopRun(
        ScriptedModel model, IReadOnlyList<LocalTool> tools, IReadOnlyList<LocalTool>? refused = null,
        bool sequential = false)
    {
        Channel = new FakeChannelConnection { ChannelId = ChatChannel, ApprovalAnswer = ToolApprovalResult.Rejected };
        var agents = new ToolLoopAgents(model, tools, refused ?? [], sequential, _lines);
        var monitor = new ChatMonitor(
            [Channel], agents, new ChatThreadResolver(_lines.Store), NoOpMetricsPublisher.Instance, null,
            NullLogger<ChatMonitor>.Instance, MonitorTestMocks.Assertions);
        _monitor = Task.Run(() => monitor.Monitor(_stop.Token));
    }

    public FakeChannelConnection Channel { get; }

    // The turns' replies, without the probes' refusals (AdmittedSoFarAsync).
    public IReadOnlyList<SendReplyParams> Replies =>
        [.. Channel.SentReplies.Where(r => r.TurnKey?.StartsWith(ProbePrefix, StringComparison.Ordinal) != true)];

    public void Send(string text, bool steer = false) => Channel.WriteMessage(new ChannelMessage
    {
        ConversationId = ConversationId,
        Content = text,
        Sender = "Fran",
        ChannelId = ChatChannel,
        AgentId = AgentId,
        Principal = new Principal(ChatChannel, Person.UserId, Person),
        Steer = steer
    });

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
    private int _probes;

    // Ends the channel's stream; the monitor returns once every turn it holds has run to its end.
    public async Task FinishAsync()
    {
        Channel.Complete();
        await _monitor.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public IReadOnlyList<string> StoredLine() => Render(_lines.Line(new AgentKey(ConversationId, AgentId).ToString()));

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
        InMemoryLines lines) : IAgentFactory
    {
        public Task<DisposableAgent> CreateAsync(
            AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler,
            Account? account = null, string? tenantId = null, CancellationToken ct = default, string? channelId = null)
        {
            var loop = new ToolApprovalChatClient(
                model, approvalHandler, agentKey.ConversationId, [.. tools.Select(t => t.Tool.Name)])
            {
                AllowConcurrentInvocation = !sequential
            };
            return Task.FromResult<DisposableAgent>(new McpAgent(
                TestAgentSpec.Named(agentKey.ConversationId) with { AgentId = agentId ?? AgentId },
                loop, lines.Store, NoOpMetricsPublisher.Instance, TimeProvider.System,
                [.. tools.Concat(refused).Select(t => t.Tool)], []));
        }

        public DisposableAgent CreateSubAgent(
            SubAgentDefinition definition, IToolApprovalHandler approvalHandler, SpawnContext spawn) =>
            throw new NotSupportedException("no worker runs in this seam yet");
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
        int step;
        lock (_requests)
        {
            step = _requests.Count;
            _requests.Add([.. messages]);
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

    private LocalTool(string name, string result, bool held)
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
                    await _finish.Task.WaitAsync(ct);
                    return result;
                },
                name),
            CallFootprint.Nothing);
    }

    public static LocalTool Answering(string name, string result) => new(name, result, held: false);

    public static LocalTool Held(string name, string result) => new(name, result, held: true);

    public AIFunction Tool { get; }

    // Bounded, so a call that never starts fails the test that expected it rather than hanging the run.
    public Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(60));

    public bool HasStarted => _started.Task.IsCompleted;

    public void Finish() => _finish.TrySetResult();

    public FunctionCallContent Call(string callId) => new(callId, Tool.Name, new Dictionary<string, object?>());
}