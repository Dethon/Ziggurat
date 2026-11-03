using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.Exceptions;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// A turn that fails — the household's key refused, out of credit, the model erroring — is kept in the
// conversation as the person's message and what they were told, so the conversation they come back to
// shows both. The model never reads a failed turn back, and a retry of it takes its place rather than
// standing beside it.
public class FailedTurnHistoryTests
{
    private const string Key = "conversation-key";
    private const string Refusal = "OpenRouter refused your household's key, so its assistant could not answer.";

    private readonly List<ChatMessage> _stored = [];
    private readonly Mock<IThreadStateStore> _store = new();
    private readonly List<int> _dropped = [];

    public FailedTurnHistoryTests()
    {
        _store.Setup(s => s.GetMessagesAsync(Key)).ReturnsAsync(() => _stored.Count == 0 ? null : [.. _stored]);
        _store.Setup(s => s.AppendMessagesAsync(Key, It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Callback<string, IReadOnlyList<ChatMessage>>((_, messages) => _stored.AddRange(messages))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.DropTailAsync(Key, It.IsAny<int>()))
            .Callback<string, int>((_, count) =>
            {
                _dropped.Add(count);
                _stored.RemoveRange(_stored.Count - count, count);
            })
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task AFailedTurn_KeepsThePersonsMessage_AndWhatTheyWereTold_BothMarkedFailed()
    {
        _stored.AddRange([new ChatMessage(ChatRole.User, "earlier"), new ChatMessage(ChatRole.Assistant, "answered")]);

        await RunAsync(new ThrowingClient(new TurnRefusedException(Refusal)), "Hola");

        _stored.Select(m => (m.Role, m.Text, m.IsFailedTurn())).ShouldBe(
        [
            (ChatRole.User, "earlier", false),
            (ChatRole.Assistant, "answered", false),
            (ChatRole.User, "Hola", true),
            (ChatRole.Assistant, Refusal, true)
        ]);
    }

    [Fact]
    public async Task AnErrorThatIsNotARefusal_IsKeptInTheWordsTheReplyShowedIt()
    {
        await RunAsync(new ThrowingClient(new InvalidOperationException("the provider fell over")), "Hola");

        _stored.Last().Text.ShouldBe("An error occurred: the provider fell over");
        _stored.Last().IsFailedTurn().ShouldBeTrue();
    }

    [Fact]
    public async Task AStoppedTurn_IsNotKeptAsFailed()
    {
        await RunAsync(new ThrowingClient(new OperationCanceledException()), "Hola");

        _stored.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheModel_NeverReadsAFailedTurnBack()
    {
        var client = new RecordingClient();
        await RunAsync(new ThrowingClient(new TurnRefusedException(Refusal)), "Hola");

        await RunAsync(client, "¿Sigues ahí?");

        client.Seen.Select(m => m.Text).ShouldBe(["¿Sigues ahí?"]);
    }

    [Fact]
    public async Task ARetryOfTheFailedTurn_TakesItsPlace()
    {
        await RunAsync(new ThrowingClient(new TurnRefusedException(Refusal)), "Hola");

        await RunAsync(new RecordingClient(), "Hola");

        _dropped.ShouldBe([2]);
        _stored.Select(m => (m.Text, m.IsFailedTurn())).ShouldBe([("Hola", false), ("answer", false)]);
    }

    [Fact]
    public async Task ADifferentMessageAfterAFailedTurn_LeavesItWhereItIs()
    {
        await RunAsync(new ThrowingClient(new TurnRefusedException(Refusal)), "Hola");

        await RunAsync(new RecordingClient(), "Otra cosa");

        _dropped.ShouldBeEmpty();
        _stored.Select(m => (m.Text, m.IsFailedTurn())).ShouldBe(
            [("Hola", true), (Refusal, true), ("Otra cosa", false), ("answer", false)]);
    }

    private async Task RunAsync(IChatClient client, string text)
    {
        var history = new RedisChatMessageStore(_store.Object);
        var agent = client.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, Key);
        try
        {
            await foreach (var _ in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, text)], session))
            {
            }
        }
        catch (Exception)
        {
            // What the turn's caller is told is the reply's business; what is kept is the store's.
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

    private sealed class RecordingClient : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}