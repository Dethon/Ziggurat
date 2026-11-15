using System.Runtime.CompilerServices;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// A turn marks its conversation in flight before it reads the line and clears the mark once it has
// written its own, so a person's rewrite is never made between the two and then written over.
public class RedisChatMessageStoreTurnInFlightTests
{
    private const string Key = "conversation-key";

    private readonly List<string> _calls = [];
    private readonly Mock<IThreadStateStore> _store = new();

    public RedisChatMessageStoreTurnInFlightTests()
    {
        _store.Setup(s => s.MarkTurnInFlightAsync(Key, It.IsAny<TimeSpan>()))
            .Callback(() => _calls.Add("mark")).Returns(Task.CompletedTask);
        _store.Setup(s => s.ClearTurnInFlightAsync(Key))
            .Callback(() => _calls.Add("clear")).Returns(Task.CompletedTask);
        _store.Setup(s => s.GetMessagesAsync(Key))
            .Callback(() => _calls.Add("read")).ReturnsAsync((ChatMessage[]?)null);
        _store.Setup(s => s.AppendMessagesAsync(Key, It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Callback(() => _calls.Add("append")).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ATurn_IsInFlightFromBeforeItsReadUntilAfterItsWrite()
    {
        await RunAsync(new AnsweringClient());

        _calls.ShouldBe(["mark", "read", "append", "clear"]);
    }

    [Fact]
    public async Task AStoppedTurn_ClearsTheMark()
    {
        await RunAsync(new ThrowingClient(new OperationCanceledException()));

        _calls.Last().ShouldBe("clear");
    }

    [Fact]
    public async Task AFailedTurn_ClearsTheMarkAfterItIsKept()
    {
        await RunAsync(new ThrowingClient(new InvalidOperationException("down")));

        _calls.ShouldBe(["mark", "read", "append", "clear"]);
    }

    private async Task RunAsync(IChatClient client)
    {
        var history = new RedisChatMessageStore(_store.Object);
        var agent = client.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, Key);
        try
        {
            await foreach (var _ in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hola")], session))
            {
            }
        }
        catch (Exception)
        {
            // The caller's to report; the mark is the store's.
        }
    }

    private sealed class ThrowingClient(Exception failure) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw failure;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw failure;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class AnsweringClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}