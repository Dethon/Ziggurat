using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// A reply the person stopped was said as far as it got: the turn keeps the person's message and what
// streamed before the stop, marked interrupted, and the model reads it from then on. A tool call is
// kept only with its result, since a call with no answer is no conversation the model can continue.
public class RedisChatMessageStoreStoppedTests
{
    private const string Key = "conversation-key";

    private readonly List<ChatMessage> _stored = [];
    private readonly Mock<IThreadStateStore> _store = new();

    public RedisChatMessageStoreStoppedTests()
    {
        _store.Setup(s => s.GetMessagesAsync(Key)).ReturnsAsync(() => _stored.Count == 0 ? null : [.. _stored]);
        _store.Setup(s => s.AppendMessagesAsync(Key, It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Callback<string, IReadOnlyList<ChatMessage>>((_, messages) => _stored.AddRange(messages))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task AStopMidText_KeepsWhatWasSaid_MarkedInterrupted()
    {
        await StopAfterAsync(new Scripted([new TextContent("Once upon ")], [new TextContent("a time")]), updatesRead: 1);

        _stored.Select(m => (m.Role.Value, m.Text, m.IsInterrupted())).ShouldBe(
            [("user", "hola", false), ("assistant", "Once upon ", true)]);
    }

    [Fact]
    public async Task AStopBeforeAnyText_KeepsThePersonsMessageAlone()
    {
        await StopAfterAsync(new Scripted([new TextReasoningContent("thinking")], [new TextContent("never")]), updatesRead: 0);

        _stored.Select(m => m.Role.Value).ShouldBe(["user"]);
    }

    [Fact]
    public async Task AStopAfterAToolCall_KeepsEveryCallWithItsResult()
    {
        await StopAfterAsync(
            new Scripted([new TextContent("Let me look. "), new FunctionCallContent("c1", "lookup")], [new TextContent("never")]),
            updatesRead: 1);

        var calls = _stored.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId);
        var results = _stored.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId);
        calls.ShouldBeSubsetOf(results);
        _stored[0].Text.ShouldBe("hola");
    }

    // The next turn's model reads what was said before the stop.
    [Fact]
    public async Task TheNextTurn_ReadsTheInterruptedReply()
    {
        await StopAfterAsync(new Scripted([new TextContent("Once upon ")], [new TextContent("a time")]), updatesRead: 1);
        var next = new Scripted([new TextContent("ok")]);

        await RunAsync(next, "sigue");

        next.Seen.Select(m => m.Text).ShouldBe(["hola", "Once upon ", "sigue"]);
    }

    // A reply that ran out of room to say more is cut off as surely as a stopped one, and is marked so.
    [Fact]
    public async Task AReplyEndedByTheOutputLimit_IsMarkedInterrupted()
    {
        var history = new RedisChatMessageStore(_store.Object);
        var agent = new LengthClient().AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, Key);
        history.BeginCollecting(session);

        await foreach (var update in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hola")], session))
        {
            history.Collect(session, update);
        }

        _stored.Select(m => (m.Text, m.IsInterrupted())).ShouldBe([("hola", false), ("Once upon", true)]);
    }

    [Fact]
    public async Task AReplyThatEnded_IsNotInterrupted()
    {
        await RunAsync(new Scripted([new TextContent("ok")]), "hola");

        _stored.ShouldAllBe(m => !m.IsInterrupted());
    }

    private sealed class LengthClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Once upon") { MessageId = "m1" };
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Length, MessageId = "m1" };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private async Task StopAfterAsync(IChatClient client, int updatesRead)
    {
        using var cts = new CancellationTokenSource();
        var history = new RedisChatMessageStore(_store.Object);
        var agent = client.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, Key);
        history.BeginCollecting(session);
        var read = 0;
        try
        {
            await foreach (var update in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hola")], session, cancellationToken: cts.Token))
            {
                history.Collect(session, update);
                if (++read >= Math.Max(1, updatesRead))
                {
                    await cts.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(IChatClient client, string text)
    {
        var history = new RedisChatMessageStore(_store.Object);
        var agent = client.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, Key);
        await foreach (var _ in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, text)], session))
        {
        }
    }

    // Streams each batch of contents as one update, waiting between batches for a stop that may come.
    private sealed class Scripted(params IReadOnlyList<AIContent>[] batches) : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, [.. batch]) { MessageId = "m1" };
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}