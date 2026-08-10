using System.Text;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// Attaching a file, watching it move, changing your mind, and sending a photo with nothing typed.
// Everything here is driven through the store's own actions against the fake connection, so a
// wiring defect between the effect, the composer and the send fails a test rather than a browser.
public sealed class ComposerAttachmentTests
{
    [Fact]
    public async Task AFilePicked_UploadsAtOnceAndBecomesReadyToSend()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("photo.png")]));

        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));
        client.Uploader.Uploaded.ShouldBe(["photo.png"]);
        client.Uploader.LastTicket.ShouldBe("ticket-1");
        Attachments(client).Single().Reference.ShouldNotBeNull();
    }

    [Fact]
    public async Task AFileStillUploading_ShowsItsOwnProgress()
    {
        await using var client = await StartAsync();
        client.Uploader.Gate = new TaskCompletionSource();
        client.Uploader.ReportProgress = [40];

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("big.png")]));

        await TestChat.Eventually(() => Attachments(client).Any(a => a.PercentComplete == 40));
        Attachments(client).Single().Status.ShouldBe(AttachmentStatus.Uploading);

        client.Uploader.Gate.SetResult();
        await TestChat.Eventually(() => Attachments(client).Single().Status == AttachmentStatus.Ready);
    }

    [Fact]
    public async Task AFileCancelledWhileUploading_LeavesTheComposer()
    {
        await using var client = await StartAsync();
        client.Uploader.Gate = new TaskCompletionSource();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("mistake.png")]));
        await TestChat.Eventually(() => Attachments(client).Count == 1);

        var attachment = Attachments(client).Single();
        client.Dispatcher.Dispatch(new RemoveAttachment("topic-1", attachment.LocalId));

        Attachments(client).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFileRemovedBeforeSending_DoesNotTravelWithTheMessage()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("photo.png")]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new RemoveAttachment("topic-1", Attachments(client).Single().LocalId));
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "never mind"));

        await TestChat.Eventually(() => SentAttachments(client) is not null);
        SentAttachments(client).ShouldBeEmpty();
    }

    [Fact]
    public async Task AMessageWithAttachmentsAndNoText_CanBeSent()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("photo.png")]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", ""));

        await TestChat.Eventually(() => SentAttachments(client) is { Count: 1 });
        SentAttachments(client)!.Single().FileName.ShouldBe("photo.png");
    }

    [Fact]
    public async Task TheSend_CarriesEveryFileThatFinishedUploading()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles(
            "topic-1", [Png("one.png"), Png("two.png")]));
        await TestChat.Eventually(() =>
            Attachments(client).Count(a => a.Status == AttachmentStatus.Ready) == 2);

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "look"));

        await TestChat.Eventually(() => SentAttachments(client) is { Count: 2 });
    }

    [Fact]
    public async Task Sending_EmptiesTheComposer()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("photo.png")]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "look"));

        await TestChat.Eventually(() => Attachments(client).Count == 0);
    }

    // The clear names the files that travelled. A file picked while the send's round trip was in
    // flight has not been sent, and sweeping the topic's whole list would throw it away silently.
    [Fact]
    public async Task AFileAttachedDuringTheSend_StaysInTheComposer()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("first.png")]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "here"));
        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("second.png")]));

        await TestChat.Eventually(() =>
            Attachments(client).Any(a => a.FileName == "second.png" && a.Status == AttachmentStatus.Ready));
        await TestChat.Eventually(() => Attachments(client).All(a => a.FileName != "first.png"));
        Attachments(client).Select(a => a.FileName).ShouldBe(["second.png"]);
    }

    // A refused file is going nowhere, so it must not spend one of the message's slots.
    [Fact]
    public async Task AFileTheComposerRefused_DoesNotConsumeAPerMessageSlot()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles(
            "topic-1", [new PickedFile("notes.txt", "text/plain", 10, Open)]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Failed));

        client.Dispatcher.Dispatch(new AttachFiles(
            "topic-1", Enumerable.Range(0, 10).Select(i => Png($"photo-{i}.png")).ToList()));

        await TestChat.Eventually(() =>
            Attachments(client).Count(a => a.Status == AttachmentStatus.Ready) == 10);
    }

    [Fact]
    public async Task AnOversizedFile_IsRefusedAtPickTimeWithoutUploading()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles(
            "topic-1", [Png("huge.png", size: 26L * 1024 * 1024)]));

        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Failed));
        client.Uploader.Uploaded.ShouldBeEmpty();
        Attachments(client).Single().Error!.ShouldContain("huge.png");
    }

    [Fact]
    public async Task AnUnsupportedKind_IsRefusedAtPickTimeWithoutUploading()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles(
            "topic-1", [new PickedFile("notes.txt", "text/plain", 10, Open)]));

        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Failed));
        client.Uploader.Uploaded.ShouldBeEmpty();
        Attachments(client).Single().Error!.ShouldContain("image or a PDF");
    }

    [Fact]
    public async Task ARefusedUpload_StaysInTheComposerAsAFailureRatherThanVanishing()
    {
        await using var client = await StartAsync();
        client.Uploader.RefuseWith = "the server said no";

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("photo.png")]));

        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Failed));
        Attachments(client).Single().Error.ShouldBe("the server said no");
    }

    // A message can be nothing but attachments now, so re-sending its text alone would ask the
    // model about a picture it was never given. The composer was emptied by the first send.
    [Fact]
    public async Task RetryingAFailedMessage_SendsItsAttachmentsAgain()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("photo.png")]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "what is this?"));
        await TestChat.Eventually(() => SentAttachments(client) is { Count: 1 });
        await TestChat.Eventually(() => Attachments(client).Count == 0);

        client.Dispatcher.Dispatch(new RetryLastMessage("topic-1"));

        await TestChat.Eventually(() => SendCalls(client) == 2);
        SentAttachments(client)!.Single().FileName.ShouldBe("photo.png");
    }

    [Fact]
    public async Task TheMessageInTheTranscript_CarriesWhatWasAttached()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("photo.png")]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "look at this"));

        await TestChat.Eventually(() =>
            client.Messages.State.MessagesByTopic.GetValueOrDefault("topic-1", [])
                .Any(m => m.Attachments is { Count: 1 }));
    }

    // The transcript is a record and not a session: what a history load holds is what a person
    // sees after a reload.
    [Fact]
    public async Task AHistoryLoad_PutsItsAttachmentsOnTheTranscript()
    {
        await using var client = await StartAsync();

        client.Service<IMessagePipeline>().LoadHistory("topic-1", [
            new ChatHistoryMessage(
                "msg-1", "user", "", "fran", DateTimeOffset.UtcNow,
                [new AttachmentReference
                {
                    Id = "7-42/abc", FileName = "photo.png", MediaType = "image/png", SizeBytes = 4
                }])
        ]);

        var loaded = client.Messages.State.MessagesByTopic["topic-1"].ShouldHaveSingleItem();
        loaded.Attachments.ShouldNotBeNull();
        loaded.Attachments!.Single().FileName.ShouldBe("photo.png");

        // A message that carried only files has no text and still has to show.
        loaded.HasContent.ShouldBeTrue();
    }

    // The ticket is what counts a message's files server-side, so a fresh one per pick would let
    // two picks of ten put twenty into one message.
    [Fact]
    public async Task SeveralPicksIntoOneMessage_ShareOneUploadTicket()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("one.png")]));
        await TestChat.Eventually(() => Attachments(client).Count(a => a.Status == AttachmentStatus.Ready) == 1);

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("two.png")]));
        await TestChat.Eventually(() => Attachments(client).Count(a => a.Status == AttachmentStatus.Ready) == 2);

        client.Transport.Calls.Count(c => c.MethodName == "CreateUploadTicket").ShouldBe(1);
    }

    // A ticket belongs to the message being composed, so the next message gets its own.
    [Fact]
    public async Task TheNextMessage_MintsItsOwnTicket()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("one.png")]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "here"));
        await TestChat.Eventually(() => Attachments(client).Count == 0);

        client.Dispatcher.Dispatch(new AttachFiles("topic-1", [Png("two.png")]));
        await TestChat.Eventually(() => Attachments(client).Any(a => a.Status == AttachmentStatus.Ready));

        client.Transport.Calls.Count(c => c.MethodName == "CreateUploadTicket").ShouldBe(2);
    }

    // A conversation started by picking a file exists before there is anything to call it, so the
    // file's name stands in. What the person types is the better name and it arrives one step
    // later, with the first message.
    [Fact]
    public async Task AConversationStartedByAFile_IsNamedAfterTheTextSentWithIt()
    {
        await using var client = await StartWithoutTopicAsync();

        client.Dispatcher.Dispatch(new AttachFiles(null, [Png("photo.png")]));
        await TestChat.Eventually(() => Started(client) is not null);

        var topicId = Started(client)!.TopicId;
        await TestChat.Eventually(() =>
            client.Composer.State.For(topicId).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new SendMessage(topicId, "what is in this picture?"));

        await TestChat.Eventually(() => Started(client)!.Name == "what is in this picture?");
        client.Transport.Calls
            .Where(c => c.MethodName == "SaveTopic")
            .Select(c => ((TopicMetadata)c.Arguments[0]!).Name)
            .Last()
            .ShouldBe("what is in this picture?");
    }

    // Nothing typed leaves the file's name as the only thing the conversation could be called.
    [Fact]
    public async Task AConversationStartedByAFileAndSentWithNoText_KeepsTheFileName()
    {
        await using var client = await StartWithoutTopicAsync();

        client.Dispatcher.Dispatch(new AttachFiles(null, [Png("photo.png")]));
        await TestChat.Eventually(() => Started(client) is not null);

        var topicId = Started(client)!.TopicId;
        await TestChat.Eventually(() =>
            client.Composer.State.For(topicId).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new SendMessage(topicId, ""));

        await TestChat.Eventually(() => SendCalls(client) == 1);
        Started(client)!.Name.ShouldBe("photo.png");
    }

    // Only the turn that opened the conversation renames it: a later message is a message, not a
    // second chance to title what is already under way.
    [Fact]
    public async Task ASecondMessage_LeavesTheNameAlone()
    {
        await using var client = await StartWithoutTopicAsync();

        client.Dispatcher.Dispatch(new AttachFiles(null, [Png("photo.png")]));
        await TestChat.Eventually(() => Started(client) is not null);

        var topicId = Started(client)!.TopicId;
        await TestChat.Eventually(() =>
            client.Composer.State.For(topicId).Any(a => a.Status == AttachmentStatus.Ready));

        client.Dispatcher.Dispatch(new SendMessage(topicId, "first"));
        await TestChat.Eventually(() => Started(client)!.Name == "first");

        client.Dispatcher.Dispatch(new SendMessage(topicId, "second"));

        await TestChat.Eventually(() => SendCalls(client) == 2);
        Started(client)!.Name.ShouldBe("first");
    }

    private static StoredTopic? Started(ScriptedChatClient client) =>
        client.Topics.State.Topics.FirstOrDefault();

    private static IReadOnlyList<ComposerAttachment> Attachments(ScriptedChatClient client) =>
        client.Composer.State.For("topic-1");

    private static int SendCalls(ScriptedChatClient client) =>
        client.Transport.Calls.Count(c => c.MethodName is "SendMessage" or "EnqueueMessage");

    private static IReadOnlyList<AttachmentReference>? SentAttachments(ScriptedChatClient client) =>
        client.Transport.Calls
            .Where(c => c.MethodName is "SendMessage" or "EnqueueMessage")
            .Select(c => c.Arguments[4] as IReadOnlyList<AttachmentReference> ?? [])
            .LastOrDefault();

    private static PickedFile Png(string name, long size = 12) =>
        new(name, "image/png", size, Open);

    private static Task<Stream> Open(CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("bytes")));

    // The composer as it is on a fresh client: an agent chosen and no conversation to attach to.
    private static async Task<ScriptedChatClient> StartWithoutTopicAsync()
    {
        var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SetAgents([
            new AgentCatalogEntry(
                "agent-1", "Agent One", null,
                DefaultModel: "sees/everything",
                DefaultModelAttachmentKinds: AttachmentKinds.All)
        ]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        return client;
    }

    private static async Task<ScriptedChatClient> StartAsync()
    {
        var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SetAgents([
            new AgentCatalogEntry(
                "agent-1", "Agent One", null,
                DefaultModel: "sees/everything",
                DefaultModelAttachmentKinds: AttachmentKinds.All)
        ]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new AddTopic(new StoredTopic
        {
            TopicId = "topic-1",
            ChatId = 7,
            ThreadId = 42,
            AgentId = "agent-1",
            Name = "Chat",
            CreatedAt = DateTime.UtcNow
        }));
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        return client;
    }
}