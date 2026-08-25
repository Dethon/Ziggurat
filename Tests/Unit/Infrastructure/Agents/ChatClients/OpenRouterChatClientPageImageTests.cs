using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// A page image gets the same window, the same forgetting and the same shape of note as one read
// off a mount, because hydration is not told which tool produced it. What differs is only what the
// note names: a page has no path to re-read, so the entry's own label stands there instead and the
// invitation is to browse again rather than to read the file again.
public class OpenRouterChatClientPageImageTests
{
    private const string Conversation = "conv-1";
    private const string Label = "A harbour at dusk";
    private static readonly byte[] Bytes = [1, 2, 3, 4];

    private readonly Mock<IChatClient> _innerClient = new();
    private readonly FakeReadImageStore _store = new();

    [Fact]
    public async Task APageImageWithinTheDistance_IsShownToTheModel()
    {
        _store.Put(Conversation, "call-1");

        var captured = await SendAsync(ConversationOfLength(6, readAt: 4), depth: 3);

        AllDataContents(captured).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task APageImagePastTheDistance_BecomesANoteNamingItAndInvitingAFreshLook()
    {
        _store.Put(Conversation, "call-1");

        var captured = await SendAsync(ConversationOfLength(6, readAt: 0), depth: 3);

        AllDataContents(captured).ShouldBeEmpty();
        var note = ResultText(captured, "call-1");
        note.ShouldContain(Label);
        note.ShouldContain("browse");
    }

    [Fact]
    public async Task TheSendAPageImageDropsOutOfView_DeletesItsStoredBytes()
    {
        _store.Put(Conversation, "call-1");

        await SendAsync(ConversationOfLength(6, readAt: 0), depth: 3);

        _store.Deleted.ShouldContain($"{Conversation}:call-1");
    }

    [Fact]
    public async Task APageImageStillInView_KeepsItsStoredBytes()
    {
        _store.Put(Conversation, "call-1");

        await SendAsync(ConversationOfLength(6, readAt: 4), depth: 3);

        _store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATurnOnAModelWithoutVision_GetsANoteAndKeepsTheBytes()
    {
        // The wire rejects a whole request carrying an image the model cannot take rather than
        // stripping it, and a later turn may be back on a model that sees.
        _store.Put(Conversation, "call-1");

        var captured = await SendAsync(ConversationOfLength(4, readAt: 2), acceptsImages: false);

        AllDataContents(captured).ShouldBeEmpty();
        ResultText(captured, "call-1").ShouldContain("does not accept images");
        _store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStoreMissWithinTheDistance_BecomesTheSameNote()
    {
        var captured = await SendAsync(ConversationOfLength(4, readAt: 2));

        AllDataContents(captured).ShouldBeEmpty();
        ResultText(captured, "call-1").ShouldContain(Label);
    }

    [Fact]
    public async Task AnEnvelopeThatAlreadySaidTheImageWasNotShown_IsLeftUntouched()
    {
        _store.Put(Conversation, "call-1");

        var captured = await SendAsync(ConversationOfLength(4, readAt: 2), shown: false);

        AllDataContents(captured).ShouldBeEmpty();
        ResultText(captured, "call-1").ShouldNotContain("no longer in view");
    }

    private static IReadOnlyList<DataContent> AllDataContents(IReadOnlyList<ChatMessage> messages) =>
        messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result)
            .OfType<List<AIContent>>()
            .SelectMany(parts => parts.OfType<DataContent>())
            .ToList();

    private static string ResultText(IReadOnlyList<ChatMessage> messages, string callId) =>
        messages
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Where(r => r.CallId == callId)
            .Select(r => r.Result switch
            {
                List<AIContent> parts => string.Join("\n", parts.OfType<TextContent>().Select(t => t.Text)),
                var other => other?.ToString() ?? ""
            })
            .Single();

    // A conversation whose tool message at `readAt` answered a view_image call, padded either side
    // so the distance rule has something to measure.
    private static List<ChatMessage> ConversationOfLength(int length, int readAt)
    {
        var messages = new List<ChatMessage>();
        foreach (var index in Enumerable.Range(0, length))
        {
            if (index == readAt)
            {
                messages.Add(new ChatMessage(ChatRole.User, "what is in that picture?"));
                messages.Add(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "mcp__websearch__view_image", null)]));
                messages.Add(new ChatMessage(ChatRole.Tool, []));
                continue;
            }

            messages.Add(new ChatMessage(index % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"turn {index}"));
        }

        return messages;
    }

    private static JsonNode Envelope(bool shown) =>
        FsResultContract.ToNode(new PageImageResult
        {
            ImageRef = "i-1",
            Label = Label,
            MediaType = "image/jpeg",
            SizeBytes = 4,
            Shown = shown
        });

    private async Task<IReadOnlyList<ChatMessage>> SendAsync(
        List<ChatMessage> messages,
        bool shown = true,
        int? depth = null,
        bool acceptsImages = true)
    {
        var toolMessage = messages.LastOrDefault(m => m.Role == ChatRole.Tool);
        if (toolMessage is not null && toolMessage.Contents.Count == 0)
        {
            toolMessage.Contents = [new FunctionResultContent("call-1", Envelope(shown))];
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
            readImageStore: _store,
            hydrationDepthMessages: depth ?? 20,
            modelAcceptsImages: _ => acceptsImages);

        await foreach (var _ in sut.GetStreamingResponseAsync(messages, Options()))
        {
        }

        return captured;
    }

    private static ChatOptions Options() =>
        new()
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ConversationContextMeta.OptionsKey] = new ConversationContext(
                    "agent", Conversation, "alice", new ReplyTarget("signalr", "topic"))
            }
        };

    private sealed class FakeReadImageStore : IReadImageStore
    {
        private readonly Dictionary<string, ReadImage> _entries = [];

        public List<string> Deleted { get; } = [];

        public void Put(string conversationId, string callId) =>
            _entries[Key(conversationId, callId)] = new ReadImage
            {
                VirtualPath = Label, MediaType = "image/jpeg", Bytes = Bytes
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