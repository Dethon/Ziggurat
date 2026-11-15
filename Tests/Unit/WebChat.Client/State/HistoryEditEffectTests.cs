using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class HistoryEditEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly ToastStore _toastStore;
    private readonly FakeTopicService _topicService = new();
    private readonly HistoryEditEffect _effect;
    private readonly StoredTopic _topic = new() { TopicId = "t", AgentId = "jonas", ChatId = 7, ThreadId = 8 };

    public HistoryEditEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _effect = new HistoryEditEffect(
            _dispatcher, _topicsStore, _messagesStore, new TopicStreams(_dispatcher, _messagesStore), _topicService,
            NullLogger<HistoryEditEffect>.Instance);
        _dispatcher.Dispatch(new AddTopic(_topic));
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "hola", EntryId = "e1" },
            new ChatMessageModel { Role = "assistant", Content = "buenas", EntryId = "e2" },
            new ChatMessageModel { Role = "assistant", Content = "network down", IsError = true }
        ]));
    }

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _toastStore.Dispose();
    }

    // The page names the last message it shows that was stored, so a line that moved on is not cut
    // from a stale view of it; then it shows the line as the server now holds it.
    [Fact]
    public async Task DeletingFromAMessage_AsksTheHub_AndShowsTheLineAsItIsNow()
    {
        _topicService.SetHistory(7, 8, new ChatHistoryMessage(null, "user", "hola", null, null, EntryId: "e1"));

        await _effect.HandleDeleteFromAsync("t", "e2");

        _topicService.DeletedFrom.ShouldHaveSingleItem().ShouldBe(("jonas", "t", 7L, "e2", "e2"));
        _messagesStore.State.MessagesByTopic["t"].Select(m => m.EntryId).ShouldBe(["e1"]);
    }

    [Fact]
    public async Task SwitchingAVersion_AsksTheHub_AndShowsTheLineAsItIsNow()
    {
        _topicService.SetHistory(7, 8, new ChatHistoryMessage(null, "user", "hola", null, null, EntryId: "e1"));

        await _effect.HandleSwitchVersionAsync("t", "e1", 0);

        _topicService.SwitchedVersions.ShouldHaveSingleItem().ShouldBe(("jonas", "t", 7L, "e1", 0, "e2"));
        _messagesStore.State.MessagesByTopic["t"].Select(m => m.EntryId).ShouldBe(["e1"]);
    }

    // The edited message and what followed are put aside first, then the edited words go out as a turn
    // with the message's own files, never whatever the composer holds.
    [Fact]
    public async Task EditAndRerun_PutsTheLineAside_ThenSendsTheEditedWordsWithTheirFiles()
    {
        var photo = new AttachmentReference { Id = "c/p", FileName = "p.png", MediaType = "image/png", SizeBytes = 1 };
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "hola", EntryId = "e1", Attachments = [photo] },
            new ChatMessageModel { Role = "assistant", Content = "buenas", EntryId = "e2" }
        ]));
        SendMessage? sent = null;
        using var _ = _dispatcher.RegisterHandler<SendMessage>(a => sent = a);

        await _effect.HandleEditAndRerunAsync("t", "e1", "hola, ¿qué tal?");

        _topicService.PutAside.ShouldHaveSingleItem().ShouldBe(("jonas", "t", 7L, "e1", "e2"));
        sent.ShouldNotBeNull();
        (sent.TopicId, sent.Message).ShouldBe(("t", "hola, ¿qué tal?"));
        sent.Attachments.ShouldBe([photo]);
    }

    // The reply is put aside after the last person's message, which is then answered again as itself.
    [Fact]
    public async Task Reroll_PutsTheReplyAside_ThenAsksForTheMessageToBeAnsweredAgain()
    {
        var patch = new AgentConfigPatch { Model = "other/model" };
        _topicService.SetHistory(7, 8, new ChatHistoryMessage(null, "user", "hola", null, null, EntryId: "e1"));
        SendRerun? asked = null;
        using var _ = _dispatcher.RegisterHandler<SendRerun>(a => asked = a);

        await _effect.HandleRerollAsync("t", patch);

        _topicService.RepliesPutAside.ShouldHaveSingleItem().ShouldBe(("jonas", "t", 7L, "e1", "e2"));
        asked.ShouldNotBeNull();
        (asked.TopicId, asked.Message, asked.RerunOf, asked.ConfigPatch).ShouldBe(("t", "hola", "e1", patch));
        asked.Attachments.ShouldBeEmpty();
    }

    // A conversation from before entry ids is stamped by the rewrite, so the message is answered again
    // by the name the history now gives it, never the legacy one the page held.
    [Fact]
    public async Task Reroll_OfALegacyConversation_AsksByTheNameTheRewriteGaveIt()
    {
        _dispatcher.Dispatch(new MessagesLoaded("t",
        [
            new ChatMessageModel { Role = "user", Content = "hola", EntryId = "legacy:0:aaa" },
            new ChatMessageModel { Role = "assistant", Content = "buenas", EntryId = "legacy:1:bbb" }
        ]));
        _topicService.SetHistory(7, 8, new ChatHistoryMessage(null, "user", "hola", null, null, EntryId: "stamped"));
        SendRerun? asked = null;
        using var _ = _dispatcher.RegisterHandler<SendRerun>(a => asked = a);

        await _effect.HandleRerollAsync("t", null);

        _topicService.RepliesPutAside.ShouldHaveSingleItem().UserEntryId.ShouldBe("legacy:0:aaa");
        asked.ShouldNotBeNull().RerunOf.ShouldBe("stamped");
    }

    // The fork opens at once, its words in the composer when it was forked from a person's message.
    [Fact]
    public async Task Fork_OpensTheNewTopic_WithTheMessagesWordsInTheComposer()
    {
        var forked = new TopicMetadata("fork-1", 70, 80, "jonas", "Chat (fork)", DateTimeOffset.UtcNow, null, OwnerId: "me");
        _topicService.ForkAnswer = new ForkedTopic(forked, "hola");
        string? offered = null;
        using var _ = _dispatcher.RegisterHandler<OfferComposerText>(a => offered = a.Text);

        await _effect.HandleForkAsync("t", "e1");

        _topicService.Forked.ShouldHaveSingleItem().ShouldBe(("jonas", "t", 7L, "e1"));
        _topicsStore.State.Topics.ShouldContain(t => t.TopicId == "fork-1");
        _topicsStore.State.SelectedTopicId.ShouldBe("fork-1");
        offered.ShouldBe("hola");
    }

    [Fact]
    public async Task EditInPlace_AsksTheHub_AndSendsNothing()
    {
        var sends = 0;
        using var _ = _dispatcher.RegisterHandler<SendMessage>(_ => sends++);

        await _effect.HandleEditInPlaceAsync("t", "e2", "corrected");

        _topicService.EditedInPlace.ShouldHaveSingleItem().ShouldBe(("jonas", "t", 7L, "e2", "corrected", "e2"));
        sends.ShouldBe(0);
    }

    [Fact]
    public async Task EditAndRerun_Refused_SendsNothing()
    {
        _topicService.ThrowOnPutAside = new HubException($"HubException: {TopicRefusals.NotRerunnable}");
        var sends = 0;
        using var _ = _dispatcher.RegisterHandler<SendMessage>(_ => sends++);

        await _effect.HandleEditAndRerunAsync("t", "e1", "otra cosa");

        sends.ShouldBe(0);
        _toastStore.State.Toasts.ShouldContain(t => t.Message == TopicRefusals.NotRerunnable);
    }

    [Fact]
    public async Task ARefusal_IsShownInTheHubsWords()
    {
        _topicService.ThrowOnDeleteFrom = new HubException(
            $"An unexpected error occurred invoking 'DeleteFrom' on the server. HubException: {TopicRefusals.Mirrored}");

        await _effect.HandleDeleteFromAsync("t", "e2");

        _toastStore.State.Toasts.ShouldContain(t => t.Message == TopicRefusals.Mirrored);
    }

    // Another page rewrote the conversation: a page showing it reads it again.
    [Fact]
    public async Task HistoryChanged_ForATopicThePageHolds_ReadsItAgain()
    {
        _topicService.SetHistory(7, 8, new ChatHistoryMessage(null, "user", "otra", null, null, EntryId: "e9"));

        await _effect.HandleHistoryChangedAsync("t");

        _messagesStore.State.MessagesByTopic["t"].Select(m => m.Content).ShouldBe(["otra"]);
    }

    // The page that rewrote the conversation shows the result already, and may already be sending the
    // edited words: the echo of its own rewrite reads nothing over them.
    [Fact]
    public async Task HistoryChanged_ForThePagesOwnRewrite_ReadsNothing()
    {
        await _effect.HandleDeleteFromAsync("t", "e2");
        _topicService.SetHistory(7, 8, new ChatHistoryMessage(null, "user", "otra", null, null, EntryId: "e9"));
        _dispatcher.Dispatch(new MessagesLoaded("t", [new ChatMessageModel { Role = "user", Content = "sending", EntryId = "x" }]));

        await _effect.HandleHistoryChangedAsync("t");

        _messagesStore.State.MessagesByTopic["t"].Select(m => m.Content).ShouldBe(["sending"]);

        await _effect.HandleHistoryChangedAsync("t");

        _messagesStore.State.MessagesByTopic["t"].Select(m => m.Content).ShouldBe(["otra"]);
    }

    [Fact]
    public async Task HistoryChanged_ForATopicThePageNeverLoaded_ReadsNothing()
    {
        await _effect.HandleHistoryChangedAsync("elsewhere");

        _messagesStore.State.MessagesByTopic.ContainsKey("elsewhere").ShouldBeFalse();
    }
}