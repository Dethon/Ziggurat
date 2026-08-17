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

// An image the model read cannot travel on the tool message that answered the read — that role's
// content is a plain string on this provider — so it arrives as its own user message straight after
// the whole tool-result message, attributed to the system because nobody sent it (ADR 0029).
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
    public async Task AnImageReadInATurn_ArrivesAsItsOwnUserMessageAfterTheWholeToolMessage()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)));

        captured.Count.ShouldBe(4);
        captured[2].Role.ShouldBe(ChatRole.Tool);
        captured[3].Role.ShouldBe(ChatRole.User);
        captured[3].Contents.OfType<DataContent>().ShouldHaveSingleItem()
            .Data.ToArray().ShouldBe(_bytes);
    }

    // Never between the results of one tool message: the function-invoking client puts every result
    // of an iteration in a single message, and some providers reject a conversation in which a tool
    // call was not answered before anything else appeared.
    [Fact]
    public async Task TheToolMessagesOwnResults_AreNeverSplitApart()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);
        _store.Put(Conversation, "call-2", CoverPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath), ("call-2", CoverPath)));

        captured[2].Contents.OfType<FunctionResultContent>().Count().ShouldBe(2);
        captured.Count(m => m.Role == ChatRole.Tool).ShouldBe(1);
    }

    [Fact]
    public async Task EachImage_IsPrecededByALabelNamingTheVirtualPathItCameFrom()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)));

        var contents = captured[3].Contents;
        var imageAt = contents.Select((c, i) => (c, i)).Single(x => x.c is DataContent).i;
        imageAt.ShouldBeGreaterThan(0);
        contents[imageAt - 1].ShouldBeOfType<TextContent>().Text.ShouldContain(ScreenshotPath);
    }

    [Fact]
    public async Task SeveralImagesReadInOneBatch_LandInOneMessageInCallOrder()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);
        _store.Put(Conversation, "call-2", CoverPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath), ("call-2", CoverPath)));

        captured.Count(m => m.Role == ChatRole.User && m.IsInjected).ShouldBe(1);

        var contents = captured[3].Contents;
        var labelled = contents
            .Select((content, index) => (content, index))
            .Where(x => x.content is DataContent)
            .Select(x => ((TextContent)contents[x.index - 1]).Text)
            .ToList();

        labelled.Count.ShouldBe(2);
        labelled[0].ShouldContain(ScreenshotPath);
        labelled[1].ShouldContain(CoverPath);
    }

    // Attributed to the system and decorated like any other turn, so the model never reads a picture
    // it went looking for as something a person said to it.
    [Fact]
    public async Task TheInjectedMessage_IsAttributedToTheSystem()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)));

        captured[3].GetSenderId().ShouldBe("system");
        captured[3].IsInjected.ShouldBeTrue();
        string.Join("", captured[3].Contents.OfType<TextContent>().Select(t => t.Text))
            .ShouldContain("Message from system");
    }

    [Fact]
    public async Task TheMessagesTheClientWasHanded_NeverGrowTheInjectedMessage()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);
        var turn = TurnThatRead(("call-1", ScreenshotPath));

        await SendAsync(turn);

        turn.Count.ShouldBe(3);
        turn.Any(m => m.IsInjected).ShouldBeFalse();
    }

    [Fact]
    public async Task AToolMessageThatReadNoImages_HasNothingInjectedAfterIt()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "search for it"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "search", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "three hits")])
        };

        var captured = await SendAsync(messages);

        captured.Count.ShouldBe(3);
    }

    // An envelope that already told the model the picture was not shown must not sprout one later:
    // the tool refused for a reason that has not changed.
    [Fact]
    public async Task AnImageTheToolCouldNotShow_HasNothingInjectedForIt()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)), shown: false);

        captured.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AClientWithNoReadImageStore_SendsTheTurnUnchanged()
    {
        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)), store: null);

        captured.Count.ShouldBe(3);
    }

    // The conversation reaches the send the same way MCP tool metadata does — on the turn's own
    // options — so a client built per model needs nothing per conversation.
    [Fact]
    public async Task ATurnCarryingNoConversationContext_InjectsNothing()
    {
        _store.Put(Conversation, "call-1", ScreenshotPath);

        var captured = await SendAsync(TurnThatRead(("call-1", ScreenshotPath)), withContext: false);

        captured.Count.ShouldBe(3);
    }

    // The truncation metric reports whose turn overflowed. An injected message is a user message
    // carrying the system as its sender, so picking the last user message naively would start
    // reporting the system for every turn in which the model looked at a file.
    [Fact]
    public async Task TheContextTruncationEvent_StillNamesThePersonAfterAnImageWasInjected()
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
        List<ChatMessage> messages, bool shown = true, bool withContext = true, int? depth = null) =>
        SendAsync(messages, _store, shown, withContext, depth);

    private async Task<IReadOnlyList<ChatMessage>> SendAsync(
        List<ChatMessage> messages,
        FakeReadImageStore? store,
        bool shown = true,
        bool withContext = true,
        int? depth = null,
        IMetricsPublisher? publisher = null,
        int? maxContextTokens = null)
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
            readImageStore: store,
            hydrationDepthMessages: depth ?? 20);

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