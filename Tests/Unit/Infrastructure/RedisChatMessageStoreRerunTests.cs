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

// A reroll answers the person's message again, which is already the last thing on the line: the turn
// carries it under its own entry id, the model reads it once, and the store does not keep it twice — so
// the versions put aside after it stay where it is.
public class RedisChatMessageStoreRerunTests
{
    private const string Key = "conversation-key";

    private readonly List<ChatMessage> _stored = [];
    private readonly Mock<IThreadStateStore> _store = new();

    public RedisChatMessageStoreRerunTests()
    {
        _store.Setup(s => s.GetMessagesAsync(Key)).ReturnsAsync(() => _stored.Count == 0 ? null : [.. _stored]);
        _store.Setup(s => s.AppendMessagesAsync(Key, It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Callback<string, IReadOnlyList<ChatMessage>>((_, messages) => _stored.AddRange(messages))
            .Returns(Task.CompletedTask);
        _store.Setup(s => s.DropTailAsync(Key, It.IsAny<int>()))
            .Callback<string, int>((_, count) => _stored.RemoveRange(_stored.Count - count, count))
            .Returns(Task.CompletedTask);
        _stored.AddRange([Stamped(ChatRole.User, "earlier", "u0"), Stamped(ChatRole.Assistant, "old", "a0"), Stamped(ChatRole.User, "hola", "u1")]);
    }

    [Fact]
    public async Task ARerun_IsReadOnce_AndKeptOnce()
    {
        var client = new AnsweringClient();

        await RunAsync(client, Rerun("hola", "u1"));

        client.Seen.Count(m => m.Text == "hola").ShouldBe(1);
        _stored.Select(m => (m.GetEntryId(), m.Text)).ShouldBe(
            [("u0", "earlier"), ("a0", "old"), ("u1", "hola"), (_stored[3].GetEntryId(), "answer")]);
    }

    // A rerun that fails leaves the person's message marked as the failed turn it was, in its place and
    // under its name, so a retry replaces it and the versions after it are still found by it.
    [Fact]
    public async Task AFailedRerun_MarksTheMessageFailedInItsPlace()
    {
        await RunAsync(new ThrowingClient(new TurnRefusedException("refused")), Rerun("hola", "u1"));

        _stored.Select(m => (m.GetEntryId(), m.IsFailedTurn())).Take(3).ShouldBe(
            [("u0", false), ("a0", false), ("u1", true)]);
        _stored.Count.ShouldBe(4);
        _stored[3].IsFailedTurn().ShouldBeTrue();

        await RunAsync(new AnsweringClient(), new ChatMessage(ChatRole.User, "hola"));

        _stored.Select(m => (m.GetEntryId(), m.IsFailedTurn())).Take(3).ShouldBe(
            [("u0", false), ("a0", false), ("u1", false)]);
        _stored.Count.ShouldBe(4);
    }

    [Fact]
    public async Task AStoppedRerun_LeavesTheMessageAsItWas()
    {
        await RunAsync(new ThrowingClient(new OperationCanceledException()), Rerun("hola", "u1"));

        _stored.Select(m => m.GetEntryId()).ShouldBe(["u0", "a0", "u1"]);
    }

    // Something was said after it between the put-aside and the turn: the rerun is an ordinary turn, its
    // message kept again under a new name rather than a second copy of the old one's.
    [Fact]
    public async Task ARerunWhoseMessageIsNoLongerLast_IsAnOrdinaryTurn()
    {
        _stored.Add(Stamped(ChatRole.Assistant, "a fire's answer", "a1"));
        var client = new AnsweringClient();

        await RunAsync(client, Rerun("hola", "u1"));

        client.Seen.Count(m => m.Text == "hola").ShouldBe(2);
        _stored.Count.ShouldBe(6);
        _stored[4].Text.ShouldBe("hola");
        _stored[4].GetEntryId().ShouldNotBe("u1");
    }

    // A legacy name is a place and a fingerprint, never a message's own: none is ever kept as one.
    [Fact]
    public async Task ALegacyName_IsNeverKept()
    {
        await RunAsync(new AnsweringClient(), Rerun("hola", "legacy:2:abcdef"));

        _stored.Select(m => m.GetEntryId()).ShouldAllBe(id => id != null && !id.StartsWith("legacy:"));
    }

    private static ChatMessage Rerun(string text, string entryId) => Stamped(ChatRole.User, text, entryId);

    private static ChatMessage Stamped(ChatRole role, string text, string entryId)
    {
        var message = new ChatMessage(role, text);
        message.SetEntryId(entryId);
        return message;
    }

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