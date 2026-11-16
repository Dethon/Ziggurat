using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Infrastructure.StateManagers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

// A rewrite changes the line and the versions beside it in one transaction, and leaves the topic's
// record saying what the new line holds: its count, every reader's position, its preview and what it is
// found by.
[Trait("Category", "Integration")]
public class RedisThreadStateStoreRewriteTests(TopicSearchFixture redis) : IClassFixture<TopicSearchFixture>
{
    private static readonly TopicViewer Anyone = new("anyone", "home");

    private readonly string _agentId = $"agent-rewrite-{Guid.NewGuid():N}";
    private readonly long _chatId = Random.Shared.NextInt64(1_000_000, 9_000_000);

    private string Key => new AgentKey($"{_chatId}:0", _agentId).ToString();

    private RedisThreadStateStore NewStore() =>
        new(redis.Connection,
            new RetentionSettings
            {
                PurgeHorizon = TimeSpan.FromDays(365), ArchiveHorizon = TimeSpan.FromDays(182), SnippetLength = 120
            },
            TimeProvider.System);

    [Fact]
    public async Task CutFrom_LeavesTheLine_TheCount_TheReadPositionsAndThePreview_InStep()
    {
        var store = await SeededAsync(("u1", "user", "plant the damsons"), ("a1", "assistant", "done"),
            ("u2", "user", "and the quinces"), ("a2", "assistant", "planted"));
        await store.MarkTopicReadAsync("home", _agentId, _chatId, "t", "reader");

        var outcome = await store.RewriteAsync(Key, new CutFrom("u2", "a2"));

        outcome.ShouldBeOfType<Rewritten>();
        (await store.GetMessagesAsync(Key))!.Select(m => m.GetEntryId()).ShouldBe(["u1", "a1"]);
        var topic = (await store.GetTopicAsync("home", _agentId, _chatId, "t"))!;
        topic.MessageCount.ShouldBe(2);
        topic.ReadBy!["reader"].ShouldBe(2);
        topic.LastMessageSnippet.ShouldBe("done");
    }

    [Fact]
    public async Task ADeletedMessage_IsNoLongerFoundBySearch()
    {
        var store = await SeededAsync(("u1", "user", "plant the damsons"), ("a1", "assistant", "done"),
            ("u2", "user", "and the quinces"), ("a2", "assistant", "planted"));
        (await store.SearchTopicsAsync(_agentId, "default", Anyone, "quinces", null, 10)).Topics.ShouldNotBeEmpty();

        await store.RewriteAsync(Key, new CutFrom("u2", "a2"));

        (await store.SearchTopicsAsync(_agentId, "default", Anyone, "quinces", null, 10)).Topics.ShouldBeEmpty();
        (await store.SearchTopicsAsync(_agentId, "default", Anyone, "damsons", null, 10)).Topics.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task VersionsPutAside_AreReadBackAndSwitchedTo()
    {
        var store = await SeededAsync(("u1", "user", "hola"), ("a1", "assistant", "first answer"));

        await store.RewriteAsync(Key, new PutAside("u1", "a1"));
        await store.AppendMessagesAsync(Key, [Message("a2", "assistant", "second answer")]);
        var conversation = await store.GetConversationAsync(Key);
        conversation.PositionAt("u1").ShouldBe(new VersionPosition(1, 2));

        await store.RewriteAsync(Key, new SwitchVersion("u1", 0, "a2"));

        (await store.GetMessagesAsync(Key))!.Select(m => m.Text).ShouldBe(["hola", "first answer"]);
        (await store.GetConversationAsync(Key)).PositionAt("u1").ShouldBe(new VersionPosition(0, 2));
    }

    [Fact]
    public async Task ARewriteFromAStaleView_IsRefused_AndChangesNothing()
    {
        var store = await SeededAsync(("u1", "user", "hola"), ("a1", "assistant", "buenas"));

        var outcome = await store.RewriteAsync(Key, new CutFrom("u1", "something-older"));

        outcome.ShouldBeOfType<RewriteRefused>().Reason.ShouldBe(RewriteRefusal.Changed);
        (await store.GetMessagesAsync(Key))!.Length.ShouldBe(2);
    }

    // A turn reads the line as it starts and appends its own as it ends; a rewrite in between would be
    // written over, so none is made while one runs.
    [Fact]
    public async Task ARewrite_WhileATurnIsInFlight_IsRefused_UntilItEnds()
    {
        var store = await SeededAsync(("u1", "user", "hola"), ("a1", "assistant", "buenas"));
        await store.MarkTurnInFlightAsync(Key, TimeSpan.FromMinutes(5));

        var during = await store.RewriteAsync(Key, new CutFrom("a1", "a1"));
        await store.ClearTurnInFlightAsync(Key);
        var after = await store.RewriteAsync(Key, new CutFrom("a1", "a1"));

        during.ShouldBeOfType<RewriteRefused>().Reason.ShouldBe(RewriteRefusal.TurnInFlight);
        after.ShouldBeOfType<Rewritten>();
    }

    [Fact]
    public async Task TheVersions_FollowTheLinesExpiry_AndGoWithIt()
    {
        var store = await SeededAsync(("u1", "user", "hola"), ("a1", "assistant", "buenas"));
        await store.RewriteAsync(Key, new PutAside("u1", "a1"));
        var db = redis.Connection.GetDatabase();
        var versionsKey = RedisThreadStateStore.VersionsKey(Key);

        (await db.KeyTimeToLiveAsync(versionsKey)).ShouldNotBeNull().TotalDays.ShouldBeGreaterThan(364);

        await store.DeleteAsync(new AgentKey($"{_chatId}:0", _agentId));

        (await db.KeyExistsAsync(versionsKey)).ShouldBeFalse();
    }

    // A reroll's versions branch after the person's message, so their place is on the reply; an edit's
    // before it, so theirs is on the person's message.
    [Fact]
    public async Task GetHistoryAsync_PutsEachSlotsVersionsOnItsFirstBubble()
    {
        var store = await SeededAsync(("u1", "user", "hola"), ("a1", "assistant", "first answer"));
        await store.RewriteAsync(Key, new PutAside("u1", "a1"));
        await store.AppendMessagesAsync(Key, [Message("a2", "assistant", "second answer")]);
        await store.RewriteAsync(Key, new PutAside(null, "a2"));
        await store.AppendMessagesAsync(Key, [Message("u3", "user", "hola otra vez"), Message("a3", "assistant", "otra")]);

        var history = await store.GetHistoryAsync(_agentId, _chatId, 0);

        history.Select(h => (h.EntryId, h.Versions)).ShouldBe(
        [
            ("u3", new MessageVersions(HistoryRewrite.Root, 1, 2)),
            ("a3", null)
        ]);

        await store.RewriteAsync(Key, new SwitchVersion(null, 0, "a3"));
        (await store.GetHistoryAsync(_agentId, _chatId, 0)).Select(h => (h.EntryId, h.Versions)).ShouldBe(
        [
            ("u1", new MessageVersions(HistoryRewrite.Root, 0, 2)),
            ("a2", new MessageVersions("u1", 1, 2))
        ]);
    }

    // A reroll that was stopped or failed left the person's message with nothing under it on the line;
    // the earlier replies are still reached through a line saying so.
    [Fact]
    public async Task GetHistoryAsync_ASlotWithNothingShown_IsALineSayingNoReply()
    {
        var store = await SeededAsync(("u1", "user", "hola"), ("a1", "assistant", "first answer"));
        await store.RewriteAsync(Key, new PutAside("u1", "a1"));

        var history = await store.GetHistoryAsync(_agentId, _chatId, 0);

        history.Count.ShouldBe(2);
        history[1].NoReply.ShouldBeTrue();
        history[1].Role.ShouldBe("assistant");
        history[1].Versions.ShouldBe(new MessageVersions("u1", 1, 2));
    }

    // A reply names the tools its turn called, so running the message before it again can say what may
    // be done again.
    [Fact]
    public async Task GetHistoryAsync_AReplyNamesTheToolsItsTurnCalled()
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t", _chatId, 0, _agentId, "Orchard", DateTimeOffset.UtcNow, null, TenantId: "home"));
        await store.AppendMessagesAsync(Key,
        [
            Message("u1", "user", "set a timer"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "mcp__timers__timer_set")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            Message("a1", "assistant", "set"),
            Message("u2", "user", "thanks"),
            Message("a2", "assistant", "you're welcome")
        ]);

        var history = await store.GetHistoryAsync(_agentId, _chatId, 0);

        history.Select(h => h.ToolsCalled).ShouldBe([null, ["timer_set"], null, null]);
    }

    // A reply cut at a tool boundary by its person's steer, or stopped between its calls, said nothing,
    // and is still a reply: it is drawn as one naming the tools it ran, and counted as one.
    [Fact]
    public async Task GetHistoryAsync_AReplyThatRanToolsAndSaidNothing_IsDrawnNamingThem()
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t", _chatId, 0, _agentId, "Orchard", DateTimeOffset.UtcNow, null, TenantId: "home"));
        await store.AppendMessagesAsync(Key,
        [
            Message("u1", "user", "look it up"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "mcp__websearch__web_search")])
            {
                MessageId = "calling"
            },
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "found")]),
            Message("u2", "user", "no, the other one"),
            Message("a2", "assistant", "the other one")
        ]);

        var history = await store.GetHistoryAsync(_agentId, _chatId, 0);

        history.Select(h => (h.Role, h.Content)).ShouldBe([
            ("user", "look it up"),
            ("assistant", ""),
            ("user", "no, the other one"),
            ("assistant", "the other one")
        ]);
        history.Select(h => h.ToolsCalled).ShouldBe([null, ["web_search"], null, null]);
        history[1].IsInterrupted.ShouldBeFalse();
        // By the id its calls streamed under, so the page that drew them names its bubble from this one.
        history[1].MessageId.ShouldBe("calling");
        (await store.GetTopicAsync("home", _agentId, _chatId, "t"))!.MessageCount.ShouldBe(4);
    }

    // A reply the person stopped between its tool calls, kept as the stop keeps one (the calls that got
    // their results, nothing of the call it was making), ends the line on those results without a word:
    // it is drawn as a reply naming the tools it ran, not marked interrupted, so no Continue is offered.
    [Fact]
    public async Task GetHistoryAsync_AReplyStoppedBetweenItsToolCalls_IsDrawnNamingTheToolsItRan()
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t", _chatId, 0, _agentId, "Orchard", DateTimeOffset.UtcNow, null, TenantId: "home"));
        var history = new RedisChatMessageStore(store);
        var model = new ModelCalls(
            [new FunctionCallContent("c1", "mcp__websearch__web_search")],
            [new TextReasoningContent("and now the other one"), new FunctionCallContent("c2", "mcp__websearch__web_search")]);
        var agent = model.AsAIAgent(new ChatClientAgentOptions
        {
            ChatHistoryProvider = history,
            ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "found", "mcp__websearch__web_search")] }
        });
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, Key);
        history.BeginCollecting(session);

        // Stopped once the second model call has begun: the first call's result is in, the second call is not.
        using var stop = new CancellationTokenSource();
        try
        {
            await foreach (var update in agent.RunStreamingAsync(
                               [new ChatMessage(ChatRole.User, "look it up")], session, cancellationToken: stop.Token))
            {
                history.Collect(session, update);
                if (update.Contents.OfType<TextReasoningContent>().Any())
                {
                    await stop.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        var shown = await store.GetHistoryAsync(_agentId, _chatId, 0);

        shown.Select(h => (h.Role, h.Content)).ShouldBe([("user", "look it up"), ("assistant", "")]);
        shown[1].ToolsCalled.ShouldBe(["web_search"]);
        shown[1].IsInterrupted.ShouldBeFalse();
        (await store.GetMessagesAsync(Key))!.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Select(c => c.CallId).ShouldBe(["c1"]);
        (await store.GetTopicAsync("home", _agentId, _chatId, "t"))!.MessageCount.ShouldBe(2);
    }

    // Answers each model call with its own contents, one update per content, yielding between them so a
    // stop can land.
    private sealed class ModelCalls(params IReadOnlyList<AIContent>[] calls) : IChatClient
    {
        private int _call;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = calls[Math.Min(Interlocked.Increment(ref _call) - 1, calls.Length - 1)];
            foreach (var content in call)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]) { MessageId = $"m{_call}" };
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task GetHistoryAsync_AReplySaysTheModelThatWroteIt()
    {
        var reply = Message("a1", "assistant", "buenas");
        reply.SetModel("other/model");
        var store = await SeededAsync(("u1", "user", "hola"));
        await store.AppendMessagesAsync(Key, [reply]);

        var history = await store.GetHistoryAsync(_agentId, _chatId, 0);

        history.Select(h => h.Model).ShouldBe([null, "other/model"]);
    }

    [Fact]
    public async Task GetHistoryAsync_AReplyCutOffSaysSo()
    {
        var reply = Message("a1", "assistant", "Once upon ");
        reply.MarkInterrupted();
        var store = await SeededAsync(("u1", "user", "hola"));
        await store.AppendMessagesAsync(Key, [reply]);

        var history = await store.GetHistoryAsync(_agentId, _chatId, 0);

        history.Select(h => h.IsInterrupted).ShouldBe([false, true]);
    }

    // A continued reply reads as the one reply it is: what came after the Continue says it is the rest of
    // the reply before, which is no longer cut off, and the hidden ask between them is neither shown nor
    // counted.
    [Fact]
    public async Task GetHistoryAsync_AContinuedReply_IsOneReply()
    {
        var cut = Message("a1", "assistant", "Once upon ");
        cut.MarkInterrupted();
        var ask = Message("c1", "user", Continuation.Instruction);
        ask.MarkContinuation();
        var store = await SeededAsync(("u1", "user", "tell me a story"));
        await store.AppendMessagesAsync(Key, [cut]);
        await store.AppendMessagesAsync(Key, [ask, Message("a2", "assistant", "a time.")]);

        var history = await store.GetHistoryAsync(_agentId, _chatId, 0);

        history.Select(h => (h.Role, h.Content, h.IsInterrupted, h.ContinuesPrevious)).ShouldBe(
        [
            ("user", "tell me a story", false, false),
            ("assistant", "Once upon ", false, false),
            ("assistant", "a time.", false, true)
        ]);
        (await store.GetTopicAsync("home", _agentId, _chatId, "t"))!.MessageCount.ShouldBe(2);
    }

    // A Continue that failed is its error, shown and counted as one; the reply it meant to carry on is
    // still cut off, for Retry to continue again.
    [Fact]
    public async Task GetHistoryAsync_AFailedContinue_IsItsErrorAndLeavesTheReplyCutOff()
    {
        var cut = Message("a1", "assistant", "Once upon ");
        cut.MarkInterrupted();
        var ask = Message("c1", "user", Continuation.Instruction);
        ask.MarkContinuation();
        ask.MarkFailedTurn();
        var error = Message("e1", "assistant", "OpenRouter refused the key.");
        error.MarkFailedTurn();
        var store = await SeededAsync(("u1", "user", "tell me a story"));
        await store.AppendMessagesAsync(Key, [cut]);
        await store.AppendMessagesAsync(Key, [ask, error]);

        var history = await store.GetHistoryAsync(_agentId, _chatId, 0);

        history.Select(h => (h.Content, h.IsInterrupted, h.IsError, h.ContinuesPrevious)).ShouldBe(
        [
            ("tell me a story", false, false, false),
            ("Once upon ", true, false, false),
            ("OpenRouter refused the key.", false, true, false)
        ]);
        (await store.GetTopicAsync("home", _agentId, _chatId, "t"))!.MessageCount.ShouldBe(3);
    }

    private async Task<RedisThreadStateStore> SeededAsync(params (string Id, string Role, string Text)[] messages)
    {
        var store = NewStore();
        await store.SaveTopicAsync(new TopicMetadata(
            "t", _chatId, 0, _agentId, "Orchard", DateTimeOffset.UtcNow, null, TenantId: "home"));
        await store.AppendMessagesAsync(Key, [.. messages.Select(m => Message(m.Id, m.Role, m.Text))]);
        return store;
    }

    private static ChatMessage Message(string id, string role, string text)
    {
        var message = new ChatMessage(new ChatRole(role), text);
        message.SetEntryId(id);
        return message;
    }
}