using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
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

    [Fact]
    public async Task HistoryChanged_ForATopicThePageNeverLoaded_ReadsNothing()
    {
        await _effect.HandleHistoryChangedAsync("elsewhere");

        _messagesStore.State.MessagesByTopic.ContainsKey("elsewhere").ShouldBeFalse();
    }
}