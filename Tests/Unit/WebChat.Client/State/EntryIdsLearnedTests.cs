using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Messages;

namespace Tests.Unit.WebChat.Client.State;

// A turn the page streamed has no entry ids until the agent has stored it, so once its stream ends
// the page reads the history again and names its bubbles from it — keeping the bubbles it drew,
// reasoning and tool calls included, which the history does not carry.
public class EntryIdsLearnedTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly MessagesStore _store;

    public EntryIdsLearnedTests() => _store = new MessagesStore(_dispatcher);

    public void Dispose() => _store.Dispose();

    [Fact]
    public void AStreamedTurn_IsNamedFromTheHistory_AndKeepsWhatItShowed()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "earlier", EntryId = "e1" },
            new ChatMessageModel { Role = "assistant", Content = "old answer", MessageId = "r0", EntryId = "e2" },
            new ChatMessageModel { Role = "user", Content = "hola", CorrelationId = "c1" },
            new ChatMessageModel { Role = "assistant", Content = "buenas", MessageId = "r1", Reasoning = "thinking" }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "earlier", "e1"),
            History("assistant", "old answer", "e2", "r0"),
            History("user", "hola", "e3"),
            History("assistant", "buenas", "e4", "r1")
        ]));

        var messages = _store.State.MessagesByTopic["t"];
        messages.Select(m => m.EntryId).ShouldBe(["e1", "e2", "e3", "e4"]);
        messages[3].Reasoning.ShouldBe("thinking");
        messages[2].CorrelationId.ShouldBe("c1");
    }

    // The same words said twice are two messages: the later bubble takes the later entry.
    [Fact]
    public void RepeatedWords_AreNamedInOrder()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "otra", EntryId = "e1" },
            new ChatMessageModel { Role = "assistant", Content = "vale", MessageId = "r0", EntryId = "e2" },
            new ChatMessageModel { Role = "user", Content = "otra" }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "otra", "e1"),
            History("assistant", "vale", "e2", "r0"),
            History("user", "otra", "e3")
        ]));

        _store.State.MessagesByTopic["t"].Select(m => m.EntryId).ShouldBe(["e1", "e2", "e3"]);
    }

    // A bubble the history does not hold — an error shown and never stored — stays unnamed.
    [Fact]
    public void ABubbleTheHistoryDoesNotHold_StaysUnnamed()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "hola" },
            new ChatMessageModel { Role = "assistant", Content = "network down", IsError = true }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t", [History("user", "hola", "e1")]));

        _store.State.MessagesByTopic["t"].Select(m => m.EntryId).ShouldBe(["e1", null]);
    }

    // What the store knows of a turn once it is kept — where it branched, what tools it called — is the
    // page's too, without a reload.
    [Fact]
    public void AStreamedTurn_LearnsItsVersionsAndToolsFromTheHistory()
    {
        var versions = new MessageVersions("u1", 1, 2);
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "hola", EntryId = "u1" },
            new ChatMessageModel { Role = "assistant", Content = "otra", MessageId = "r2" }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "hola", "u1"),
            History("assistant", "otra", "a2", "r2") with { Versions = versions, ToolsCalled = ["timer_set"] }
        ]));

        var reply = _store.State.MessagesByTopic["t"][1];
        reply.Versions.ShouldBe(versions);
        reply.ToolsCalled.ShouldBe(["timer_set"]);
    }

    // The "No reply" line stood for the answer that has now streamed in, and the history no longer has it.
    [Fact]
    public void ANoReplyLineTheHistoryNoLongerHas_Goes()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "hola", EntryId = "u1" },
            new ChatMessageModel { Role = "assistant", NoReply = true, Versions = new MessageVersions("u1", 0, 1) },
            new ChatMessageModel { Role = "assistant", Content = "otra", MessageId = "r2" }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t", [History("user", "hola", "u1"), History("assistant", "otra", "a2", "r2")]));

        _store.State.MessagesByTopic["t"].Select(m => m.EntryId).ShouldBe(["u1", "a2"]);
    }

    // A rerun that was stopped again leaves the line the history now draws.
    [Fact]
    public void ANoReplyLineTheHistoryHas_IsDrawn()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t", [new ChatMessageModel { Role = "user", Content = "hola", EntryId = "u1" }]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "hola", "u1"),
            new ChatHistoryMessage(null, "assistant", "", null, null, Versions: new MessageVersions("u1", 1, 2), NoReply: true)
        ]));

        _store.State.MessagesByTopic["t"].Select(m => m.NoReply).ShouldBe([false, true]);
    }

    // Two people's turns in a shared topic: the page drew both messages before both replies, the store
    // kept each reply after its message. The page keeps its order and draws nothing twice — two bubbles
    // with one provider id break the next resume's rebuild.
    [Fact]
    public void TurnsThePageDrewInAnotherOrder_AreNamed_AndNothingIsDrawnTwice()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "ana" },
            new ChatMessageModel { Role = "user", Content = "carlos" },
            new ChatMessageModel { Role = "assistant", Content = "OK", MessageId = "m1" },
            new ChatMessageModel { Role = "assistant", Content = "OK", MessageId = "m2" }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "ana", "e1"),
            History("assistant", "OK", "e2", "m1"),
            History("user", "carlos", "e3"),
            History("assistant", "OK", "e4", "m2")
        ]));

        _store.State.MessagesByTopic["t"].Select(m => (m.Content, m.EntryId)).ShouldBe(
            [("ana", "e1"), ("carlos", "e3"), ("OK", "e2"), ("OK", "e4")]);
    }

    private static ChatHistoryMessage History(string role, string content, string entryId, string? messageId = null) =>
        new(messageId, role, content, null, null, EntryId: entryId);
}