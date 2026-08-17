using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// An image the model read travels inside the tool result that answered the read: the Responses
// wire accepts content parts in a function call output, so the envelope is followed by the picture
// itself, in the one place the model already looks for the answer (ADR 0029).
//
// This is the seam hydration is tested at, which is why the widened pass has no suite of its own.
public class OpenRouterChatClientReadImageTests
{
    private const string Conversation = "conv-1";
    private const string ScreenshotPath = "/vault/shots/error.png";
    private const string CoverPath = "/media/films/cover.jpg";

    private static readonly byte[] _bytes = [1, 2, 3, 4];

    private readonly Mock<IChatClient> _innerClient = new();
    private readonly FakeReadImageStore _store = new();

    [Fact]
    public async Task AnImageReadInATurn_ArrivesInsideItsOwnToolResult()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)));

        captured.Count.ShouldBe(3);
        var contents = ResultContents(captured, "call-1");
        contents.OfType<DataContent>().ShouldHaveSingleItem().Data.ToArray().ShouldBe(_bytes);
    }

    // The envelope the tool answered still reaches the model ahead of the picture, so the path,
    // media type and size stay quotable into the next tool call.
    [Fact]
    public async Task TheEnvelope_PrecedesTheImageInsideTheResult()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)));

        var contents = ResultContents(captured, "call-1");
        var imageAt = contents.Select((c, i) => (c, i)).Single(x => x.c is DataContent).i;
        imageAt.ShouldBeGreaterThan(0);
        contents.Take(imageAt).OfType<TextContent>()
            .ShouldContain(t => t.Text.Contains(ScreenshotPath));
    }

    [Fact]
    public async Task SeveralImagesReadInOneBatch_EachRideTheirOwnResult()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);
        _store.Put(Conversation, "call-2", CoverPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath), ("call-2", CoverPath)));

        captured.Count.ShouldBe(3);
        ResultContents(captured, "call-1").OfType<DataContent>().ShouldHaveSingleItem();
        ResultContents(captured, "call-2").OfType<DataContent>().ShouldHaveSingleItem();
        ResultText(captured, "call-1").ShouldContain(ScreenshotPath);
        ResultText(captured, "call-2").ShouldContain(CoverPath);
    }

    [Fact]
    public async Task TheMessagesTheClientWasHanded_AreNeverMutated()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);
        var turn = TurnThatRead(("call-1", ScreenshotPath));

        await SendAsync(turn);

        turn.Count.ShouldBe(3);
        turn[2].Contents.OfType<FunctionResultContent>().Single().Result
            .ShouldBeOfType<JsonObject>();
    }

    [Fact]
    public async Task AToolMessageThatReadNoImages_IsLeftAlone()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "search for it"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "three hits")])
        };

        var captured = await SendAsync(messages);

        captured.Count.ShouldBe(3);
        captured[2].Contents.OfType<FunctionResultContent>().Single().Result.ShouldBe("three hits");
    }

    // An envelope that already told the model the picture was not shown must not sprout one later:
    // the tool refused for a reason that has not changed.
    [Fact]
    public async Task AnImageTheToolCouldNotShow_KeepsItsEnvelopeUntouched()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)), shown: false);

        captured[2].Contents.OfType<FunctionResultContent>().Single().Result
            .ShouldBeOfType<JsonObject>();
    }

    // "Out of depth, expired, evicted, or no store at all" is one answer, not three plus a silence:
    // the envelope already promised the model a picture, so a send that cannot produce one has to
    // say which image it cannot show rather than quietly dropping it.
    [Fact]
    public async Task AClientWithNoReadImageStore_StillNamesTheImageItCannotShow()
    {
        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)), store: null);

        var result = ResultText(captured, "call-1");
        result.ShouldContain(ScreenshotPath);
        result.ShouldContain("Read the file again");
    }

    // The tool keys its write on the agent's own conversation id and the send keys its read on the
    // turn's options, so a turn that carries no context cannot find bytes that really were written.
    [Fact]
    public async Task ATurnCarryingNoConversationContext_NamesTheImageItCannotShow()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)), withContext: false);

        ResultText(captured, "call-1").ShouldContain(ScreenshotPath);
        AllDataContents(captured).ShouldBeEmpty();
    }

    // An image stays in front of the model for the rest of the exchange about it, on the same
    // distance an attachment lives for, and then gets out of the way.
    [Fact]
    public async Task AnImageWithinTheDistance_IsStillShown()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(ConversationOfLength(6, readAt: 4), depth: 3);

        AllDataContents(captured).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnImagePastTheDistance_BecomesAPlaceholderNamingThePathAndInvitingAReread()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(ConversationOfLength(6, readAt: 0), depth: 3);

        AllDataContents(captured).ShouldBeEmpty();
        var placeholder = ResultText(captured, "call-1");
        placeholder.ShouldContain(ScreenshotPath);
        placeholder.ShouldContain("Read the file again");
    }

    // The message window is the real bound, so the bytes go on the send the image drops out of view
    // rather than waiting out the store's own horizon.
    [Fact]
    public async Task TheSendAnImageDropsOutOfView_DeletesItsStoredBytes()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        await SendAsync(ConversationOfLength(6, readAt: 0), depth: 3);

        _store.Deleted.ShouldContain($"{Conversation}:call-1");
    }

    // The envelope of a dropped image stays in the history for the rest of the conversation, so
    // without a memo every later send would re-issue the same delete forever.
    [Fact]
    public async Task TheSendsAfterAnImageDropped_DoNotDeleteItAgain()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);
        var messages = ConversationOfLength(6, readAt: 0);
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());
        var sut = new OpenRouterChatClient(
            _innerClient.Object, "test-model", readImageStore: _store, hydrationDepthMessages: 3);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            await foreach (var __ in sut.GetStreamingResponseAsync(messages, Options(withContext: true)))
            {
            }
        }

        _store.Deleted.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnImageStillInView_KeepsItsStoredBytes()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        await SendAsync(ConversationOfLength(6, readAt: 4), depth: 3);

        _store.Deleted.ShouldBeEmpty();
    }

    // A turn patched onto a model without vision must not carry image parts — the wire rejects the
    // whole request rather than stripping them — and must not burn the bytes either, because the
    // next turn may be back on a model that sees.
    [Fact]
    public async Task ATurnOnAModelWithoutVision_GetsAPlaceholderAndKeepsTheBytes()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)), acceptsImages: false);

        AllDataContents(captured).ShouldBeEmpty();
        var placeholder = ResultText(captured, "call-1");
        placeholder.ShouldContain(ScreenshotPath);
        placeholder.ShouldContain("does not accept images");
        _store.Deleted.ShouldBeEmpty();
    }

    // Expired, evicted, or never written at all: the model is told which image it lost rather than
    // left to invent what was in it.
    [Fact]
    public async Task AStoreMissWithinTheDistance_BecomesTheSamePlaceholder()
    {
        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)));

        var placeholder = ResultText(captured, "call-1");
        placeholder.ShouldContain(ScreenshotPath);
        placeholder.ShouldContain("Read the file again");
    }

    // Protecting the person: a picture the model went looking for must never push out a photo they
    // actually sent. Tool messages are excluded from the distance count, so twenty reads move an
    // attachment no further away.
    [Fact]
    public async Task ImagesTheModelRead_DoNotShortenHowLongAPersonsAttachmentIsHydrated()
    {
        var messages = new List<ChatMessage> { UserTurnWithPhoto() };
        foreach (var callId in new[] { "call-1", "call-2", "call-3" })
        {
            _store.Put(Conversation, callId, ScreenshotPath);
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, "domain__filesystem__file_read", null)]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callId, Envelope(ScreenshotPath, shown: true))]));
        }

        var captured = await SendAsync(
            messages, _store, depth: 2, attachments: new StubAttachmentSource());

        captured[0].Contents.OfType<DataContent>().ShouldHaveSingleItem()
            .MediaType.ShouldBe("image/png");
    }

    // The truncation metric reports whose turn overflowed, and nothing this pass adds is a user
    // message, so the last user message stays the person's.
    [Fact]
    public async Task TheContextTruncationEvent_StillNamesThePersonAfterAnImageWasShown()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);
        var messages = TurnThatRead(("call-1", ScreenshotPath));
        messages[0] = new ChatMessage(ChatRole.User, new string('a', 4000));
        messages[0].SetSenderId("alice");

        ContextTruncationEvent? published = null;
        var publisher = new Mock<IMetricsPublisher>();
        publisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is ContextTruncationEvent t)
                {
                    published = t;
                }
            });

        await SendAsync(messages, _store, publisher: publisher.Object, maxContextTokens: 80);

        published.ShouldNotBeNull();
        published.Sender.ShouldBe("alice");
    }

    private static ChatMessage UserTurnWithPhoto()
    {
        var message = new ChatMessage(ChatRole.User, "what is in this photo?");
        message.SetAttachments([
            new AttachmentReference
            {
                Id = "7-42/abc", FileName = "photo.png", MediaType = "image/png", SizeBytes = 4
            }
        ]);
        message.SetAttachmentChannelId("signalr");
        return message;
    }

    // A conversation long enough for the distance to bite, with the read happening at a chosen point
    // in it. Only the plain user and assistant turns move anything further away.
    private List<ChatMessage> ConversationOfLength(int length, int readAt)
    {
        var messages = new List<ChatMessage>();
        for (var index = 0; index < length; index++)
        {
            if (index == readAt)
            {
                messages.Add(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "domain__filesystem__file_read", null)]));
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent("call-1", Envelope(ScreenshotPath, shown: true))]));
                continue;
            }

            messages.Add(new ChatMessage(
                index % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"message {index}"));
        }

        return messages;
    }

    private static FunctionResultContent Result(IReadOnlyList<ChatMessage> captured, string callId) =>
        captured
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == callId);

    private static IReadOnlyList<AIContent> ResultContents(
        IReadOnlyList<ChatMessage> captured, string callId) =>
        Result(captured, callId).Result.ShouldBeAssignableTo<IEnumerable<AIContent>>()!.ToList();

    // The result's readable text, whatever shape the rewrite chose for it.
    private static string ResultText(IReadOnlyList<ChatMessage> captured, string callId) =>
        Result(captured, callId).Result switch
        {
            string s => s,
            IEnumerable<AIContent> contents => string.Join(
                "", contents.OfType<TextContent>().Select(t => t.Text)),
            var other => other?.ToString() ?? ""
        };

    private static IReadOnlyList<DataContent> AllDataContents(IReadOnlyList<ChatMessage> captured) =>
        captured
            .SelectMany(m => m.Contents)
            .SelectMany(c => c switch
            {
                DataContent d => [d],
                FunctionResultContent { Result: IEnumerable<AIContent> inner } =>
                    inner.OfType<DataContent>(),
                _ => Enumerable.Empty<DataContent>()
            })
            .ToList();

    private List<ChatMessage> TurnThatRead(params (string CallId, string Path)[] reads) =>
    [
        new(ChatRole.User, "what does the error say?"),
        new(ChatRole.Assistant,
            reads.Select(r => (AIContent)new FunctionCallContent(
                r.CallId, "domain__filesystem__file_read", null)).ToList()),
        new(ChatRole.Tool, [])
    ];

    private static JsonNode Envelope(string path, bool shown) =>
        FsResultContract.ToNode(new FsImageReadResult
        {
            FilePath = path,
            MediaType = "image/png",
            SizeBytes = 4,
            Shown = shown
        });

    private Task<IReadOnlyList<ChatMessage>> SendAsync(
        List<ChatMessage> messages,
        bool shown = true,
        bool withContext = true,
        int? depth = null,
        bool acceptsImages = true) =>
        SendAsync(messages, _store, shown, withContext, depth, acceptsImages: acceptsImages);

    private async Task<IReadOnlyList<ChatMessage>> SendAsync(
        List<ChatMessage> messages,
        FakeReadImageStore? store,
        bool shown = true,
        bool withContext = true,
        int? depth = null,
        bool acceptsImages = true,
        IMetricsPublisher? publisher = null,
        int? maxContextTokens = null,
        IAttachmentSource? attachments = null)
    {
        // Filled in here rather than in the builder so one harness serves the shown and not-shown
        // cases without two nearly identical conversation builders.
        var toolMessage = messages.LastOrDefault(m => m.Role == ChatRole.Tool);
        if (toolMessage is not null && toolMessage.Contents.Count == 0)
        {
            var calls = messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();
            toolMessage.Contents = calls
                .Select(c => (AIContent)new FunctionResultContent(
                    c.CallId, Envelope(PathOf(c.CallId), shown)))
                .ToList();
        }

        IReadOnlyList<ChatMessage> captured = [];
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, _, _) => captured = msgs.ToList())
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        var sut = new OpenRouterChatClient(
            _innerClient.Object,
            "test-model",
            maxContextTokens: maxContextTokens,
            metricsPublisher: publisher,
            attachmentSource: attachments,
            readImageStore: store,
            hydrationDepthMessages: depth ?? 20,
            modelAcceptsImages: _ => acceptsImages);

        await foreach (var _ in sut.GetStreamingResponseAsync(messages, Options(withContext)))
        {
        }

        return captured;
    }

    private static string PathOf(string callId) => callId == "call-2" ? CoverPath : ScreenshotPath;

    private static ChatOptions Options(bool withContext) =>
        new()
        {
            AdditionalProperties = withContext
                ? new AdditionalPropertiesDictionary
                {
                    [ConversationContextMeta.OptionsKey] = new ConversationContext(
                        "agent", Conversation, "alice", new ReplyTarget("signalr", "topic"))
                }
                : null
        };

    private sealed class StubAttachmentSource : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct) =>
            Task.FromResult<byte[]?>(_bytes);
    }

    private sealed class FakeReadImageStore : IReadImageStore
    {
        private readonly Dictionary<string, ReadImage> _entries = [];

        public List<string> Deleted { get; } = [];

        public void Put(string conversationId, string callId, string virtualPath) =>
            _entries[Key(conversationId, callId)] = new ReadImage
            {
                VirtualPath = virtualPath, MediaType = "image/png", Bytes = _bytes
            };

        public Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct)
        {
            _entries[Key(conversationId, callId)] = image;
            return Task.CompletedTask;
        }

        public Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.FromResult(_entries.GetValueOrDefault(Key(conversationId, callId)));

        public Task DeleteAsync(string conversationId, string callId, CancellationToken ct)
        {
            var key = Key(conversationId, callId);
            Deleted.Add(key);
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        private static string Key(string conversationId, string callId) => $"{conversationId}:{callId}";
    }
}