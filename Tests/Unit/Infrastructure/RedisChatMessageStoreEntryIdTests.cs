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

// Every message a turn keeps is given an entry id of its own as it is stored: how a page names one
// message to reroll, edit, delete or fork from, since the provider's MessageId is one id per model
// call and a person's message has none.
public class RedisChatMessageStoreEntryIdTests
{
    private const string Key = "conversation-key";

    private readonly List<ChatMessage> _stored = [];
    private readonly Mock<IThreadStateStore> _store = new();

    public RedisChatMessageStoreEntryIdTests()
    {
        _store.Setup(s => s.GetMessagesAsync(Key)).ReturnsAsync(() => _stored.Count == 0 ? null : [.. _stored]);
        _store.Setup(s => s.AppendMessagesAsync(Key, It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Callback<string, IReadOnlyList<ChatMessage>>((_, messages) => _stored.AddRange(messages))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.DropTailAsync(Key, It.IsAny<int>()))
            .Callback<string, int>((_, count) => _stored.RemoveRange(_stored.Count - count, count))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task EveryStoredMessage_CarriesADistinctEntryId()
    {
        await RunAsync(new AnsweringClient(), "Hola");
        await RunAsync(new AnsweringClient(), "Otra");

        var ids = _stored.Select(m => m.GetEntryId()).ToList();
        ids.ShouldAllBe(id => !string.IsNullOrEmpty(id));
        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public async Task AFailedTurn_IsStoredWithEntryIds()
    {
        await RunAsync(new ThrowingClient(new TurnRefusedException("refused")), "Hola");

        _stored.ShouldAllBe(m => !string.IsNullOrEmpty(m.GetEntryId()));
    }

    [Fact]
    public async Task ARetry_KeepsTheEntryIdOfTheMessageItReplaces()
    {
        await RunAsync(new ThrowingClient(new TurnRefusedException("refused")), "Hola");
        var failedId = _stored[0].GetEntryId();

        await RunAsync(new AnsweringClient(), "Hola");

        _stored[0].Text.ShouldBe("Hola");
        _stored[0].GetEntryId().ShouldBe(failedId);
    }

    [Fact]
    public async Task AMessageThatAlreadyHasAnEntryId_KeepsIt()
    {
        var message = new ChatMessage(ChatRole.User, "Hola");
        message.SetEntryId("given");

        await RunAsync(new AnsweringClient(), message);

        _stored[0].GetEntryId().ShouldBe("given");
    }

    private Task RunAsync(IChatClient client, string text) => RunAsync(client, new ChatMessage(ChatRole.User, text));

    private async Task RunAsync(IChatClient client, ChatMessage message)
    {
        var history = new RedisChatMessageStore(_store.Object);
        var agent = client.AsAIAgent(new ChatClientAgentOptions { ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, Key);
        try
        {
            await foreach (var _ in agent.RunStreamingAsync([message], session))
            {
            }
        }
        catch (Exception)
        {
            // What is kept is the store's business, not what the caller was told.
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