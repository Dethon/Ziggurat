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

    // A reply that ended on a call the person turned down streamed no words: the history draws the line
    // said in its place under the id its answer streamed by, so once it is kept the page shows it too, as a
    // reload would.
    [Fact]
    public void AStreamedMessageThatDrewNoWords_TakesTheWordsTheHistoryHasForIt()
    {
        var declined = new ToolCallStep { CallId = "c1", Result = "rejected", Outcome = ToolCallOutcome.Declined };
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "delete it", CorrelationId = "c1" },
            new ChatMessageModel { Role = "assistant", MessageId = "t1", Tools = [declined] }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "delete it", "e1"),
            History("assistant", "delete was not run: it was turned down.", "e2", "t1")
        ]));

        var reply = _store.State.MessagesByTopic["t"][1];
        (reply.Content, reply.EntryId).ShouldBe(("delete was not run: it was turned down.", "e2"));
        reply.Tools.ShouldBe([declined]);
    }

    // Another person's turn in a shared topic that no wire brought here — begun and answered while this
    // page's own send held the topic — is in the history: it goes in where the history has it, and
    // nothing the page drew is taken away, its own message not yet stored included.
    [Fact]
    public void ATurnNoWireBroughtHere_GoesInWhereTheHistoryHasIt()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "earlier", EntryId = "e1" },
            new ChatMessageModel { Role = "assistant", Content = "old answer", MessageId = "r0", EntryId = "e2" },
            new ChatMessageModel { Role = "user", Content = "mine", CorrelationId = "c1" },
            new ChatMessageModel { Role = "assistant", Content = "OK", MessageId = "r2" },
            new ChatMessageModel { Role = "user", Content = "mine again", CorrelationId = "c2" }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "earlier", "e1"),
            History("assistant", "old answer", "e2", "r0"),
            History("user", "Ana's", "e3"),
            History("assistant", "OK", "e4", "r1"),
            History("user", "mine", "e5"),
            History("assistant", "OK", "e6", "r2")
        ], _store.State.Revision));

        var messages = _store.State.MessagesByTopic["t"];
        messages.Select(m => m.Content).ShouldBe(["earlier", "old answer", "Ana's", "OK", "mine", "OK", "mine again"]);
        messages.Select(m => m.EntryId).ShouldBe(["e1", "e2", "e3", "e4", "e5", "e6", null]);
        _store.State.IsFinalized("t", "r1").ShouldBeTrue();
    }

    // A reply the page drew and could not name is no missed turn: only a person's message the page never
    // drew brings anything in, so nothing is drawn twice.
    [Fact]
    public void AReplyThePageCouldNotName_IsNotDrawnAgain()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "hola", EntryId = "e1" },
            new ChatMessageModel { Role = "assistant", Content = "buenas, y m" }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "hola", "e1"),
            History("assistant", "buenas, y más", "e2", "r1")
        ], _store.State.Revision));

        _store.State.MessagesByTopic["t"].Select(m => m.Content).ShouldBe(["hola", "buenas, y m"]);
    }

    // A page that drew another person's message from a resume's buffer, whose wire then opened after the
    // reply ended, holds the message and never the reply: the reply goes in under it.
    [Fact]
    public void AReplyNoWireBroughtUnderAMessageThePageShows_GoesInUnderIt()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "Ana's", EntryId = "e1" },
            new ChatMessageModel { Role = "user", Content = "mine", CorrelationId = "c1" },
            new ChatMessageModel { Role = "assistant", Content = "OK", MessageId = "r2" }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "Ana's", "e1"),
            History("assistant", "Sure", "e2", "r1"),
            History("user", "mine", "e3"),
            History("assistant", "OK", "e4", "r2")
        ], _store.State.Revision));

        _store.State.MessagesByTopic["t"].Select(m => m.Content).ShouldBe(["Ana's", "Sure", "mine", "OK"]);
    }

    // An error the page showed is never named, so the history's copy of it is no missed reply.
    [Fact]
    public void AnErrorThePageShowed_IsNotDrawnAgain()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "hola", EntryId = "e1" },
            new ChatMessageModel { Role = "assistant", Content = "It failed", IsError = true }
        ]));

        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "hola", "e1"),
            new ChatHistoryMessage(null, "assistant", "It failed", null, null, IsError: true, EntryId: "e2")
        ], _store.State.Revision));

        _store.State.MessagesByTopic["t"].Select(m => m.Content).ShouldBe(["hola", "It failed"]);
    }

    // A read asked for before the person deleted a turn arrives after the delete redrew the page: it still
    // names what it can, and puts back nothing the delete took away.
    [Fact]
    public void AReadFromBeforeADelete_PutsNothingBack()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "keep", EntryId = "e1" },
            new ChatMessageModel { Role = "assistant", Content = "kept", MessageId = "r0", EntryId = "e2" },
            new ChatMessageModel { Role = "user", Content = "gone", EntryId = "e3" },
            new ChatMessageModel { Role = "assistant", Content = "gone too", MessageId = "r1", EntryId = "e4" }
        ]));
        var asked = _store.State.Revision;

        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "keep", EntryId = "e1" },
            new ChatMessageModel { Role = "assistant", Content = "kept", MessageId = "r0", EntryId = "e2" }
        ]));
        _dispatcher.Dispatch(new EntryIdsLearned("t",
        [
            History("user", "keep", "e1"),
            History("assistant", "kept", "e2", "r0"),
            History("user", "gone", "e3"),
            History("assistant", "gone too", "e4", "r1")
        ], asked));

        _store.State.MessagesByTopic["t"].Select(m => m.Content).ShouldBe(["keep", "kept"]);
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