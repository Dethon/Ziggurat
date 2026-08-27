using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Web;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// The bridge writes a picture's bytes under an index, and hydration reads them back by computing
// that index again from the text. Two files, one convention -- so it is pinned here by driving
// both halves over the shape the server actually emits, rather than by two comments agreeing.
//
// This is the seam a review caught: the bridge counted pictures while hydration counted JSON
// blocks, the call's own envelope took index 0, and every image hydrated as "no longer in view".
public class PageImageRoundTripTests
{
    private const string Conversation = "conv-1";
    private const string CallId = "call-7";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public async Task EveryPictureTheServerReturns_IsFoundAgainByHydration(int pictures)
    {
        var store = new RecordingStore();
        var result = await McpImageLift.ApplyAsync(
            AsAgentSees(ServerResult(pictures)), store, Conversation, CallId, CancellationToken.None);

        // What the model's turn actually carries after the bridge and Flatten have had it.
        var flattened = QualifiedMcpTool.Flatten(result);
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(CallId, flattened)]);

        var reads = KeysHydrationWillAskFor(message);

        reads.Count.ShouldBe(pictures);
        // The keys hydration will ask the store for must be keys the bridge actually wrote.
        reads.ShouldBeSubsetOf(store.Keys);
    }

    [Fact]
    public async Task ARefusedPictureAmongTheGood_DoesNotShiftTheOthersKeys()
    {
        // A refusal is an envelope with no picture after it. It still occupies a block, so both
        // sides have to count it the same way or everything after it moves.
        var store = new RecordingStore();
        var blocks = new List<ContentBlock>
        {
            Text("""{"status":"success","sessionId":"s","imageCount":2}"""),
            Text(ImageEnvelope("A refused picture", shown: false)),
            Text(ImageEnvelope("A good picture", shown: true)),
            Image()
        };

        var result = await McpImageLift.ApplyAsync(
            AsAgentSees(blocks), store, Conversation, CallId, CancellationToken.None);

        var message = new ChatMessage(
            ChatRole.Tool, [new FunctionResultContent(CallId, QualifiedMcpTool.Flatten(result))]);

        var reads = KeysHydrationWillAskFor(message);

        reads.ShouldHaveSingleItem().ShouldBe(store.Keys.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task AForeignServersBareImage_IsStillFoundByHydration()
    {
        // A server that answers with nothing but an image block -- no envelope, no text at all.
        // The bridge synthesizes the envelope, and the result is then a single text block, which
        // Flatten leaves as a one-element list rather than a string: hydration has to read that
        // shape too, or the picture is stored and never asked for.
        var store = new RecordingStore();
        object result = new AIContent[] { new DataContent(new byte[] { 4, 5 }, "image/png") };

        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, CallId, CancellationToken.None);

        var flattened = QualifiedMcpTool.Flatten(lifted);
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(CallId, flattened)]);

        var reads = KeysHydrationWillAskFor(message);

        reads.ShouldHaveSingleItem().ShouldBe(store.Keys.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task AForeignBlockWithAnEmbeddedJsonParagraph_DoesNotShiftTheKeys()
    {
        // Flatten joins text blocks with a blank line and hydration splits the joined text on it,
        // so a '{'-paragraph embedded inside one block is a candidate over there whether or not it
        // was ever its own block. The bridge has to count the same way, or everything after that
        // paragraph is stored one key off its bytes.
        var store = new RecordingStore();
        object result = new AIContent[]
        {
            new TextContent("Some prose the server wrote.\n\n{\"anything\": 1}"),
            new DataContent(new byte[] { 4, 5 }, "image/png")
        };

        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, CallId, CancellationToken.None);
        var message = new ChatMessage(
            ChatRole.Tool, [new FunctionResultContent(CallId, QualifiedMcpTool.Flatten(lifted))]);

        var reads = KeysHydrationWillAskFor(message);

        reads.ShouldHaveSingleItem().ShouldBe(store.Keys.ShouldHaveSingleItem());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public async Task EveryPicture_IsStillFoundAfterAHistoryReload(int pictures)
    {
        // Every turn's history comes back from Redis through a plain JsonSerializer round trip,
        // which hands hydration a JsonElement where the live turn held a string or a content list.
        // A picture that is only recognised on the turn that fetched it is a picture the model
        // sees once and then stares at "shown": true envelopes with nothing behind them.
        var store = new RecordingStore();
        var result = await McpImageLift.ApplyAsync(
            AsAgentSees(ServerResult(pictures)), store, Conversation, CallId, CancellationToken.None);

        var message = Reloaded(new ChatMessage(
            ChatRole.Tool, [new FunctionResultContent(CallId, QualifiedMcpTool.Flatten(result))]));

        var reads = KeysHydrationWillAskFor(message);

        reads.Count.ShouldBe(pictures);
        reads.ShouldBeSubsetOf(store.Keys);
    }

    [Fact]
    public async Task AForeignServersBareImage_IsStillFoundAfterAHistoryReload()
    {
        // The single-block shape Flatten leaves as a list comes back from the store as a JSON
        // array of text contents, not as an IList<AIContent>.
        var store = new RecordingStore();
        object result = new AIContent[] { new DataContent(new byte[] { 4, 5 }, "image/png") };

        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, CallId, CancellationToken.None);
        var message = Reloaded(new ChatMessage(
            ChatRole.Tool, [new FunctionResultContent(CallId, QualifiedMcpTool.Flatten(lifted))]));

        var reads = KeysHydrationWillAskFor(message);

        reads.ShouldHaveSingleItem().ShouldBe(store.Keys.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task AReloadedResult_RewritesWithTheEnvelopeTextItself()
    {
        // The envelope the model reads back must be the text the bridge left, not that text
        // re-serialized as a JSON string with escaped quotes.
        var store = new RecordingStore();
        var result = await McpImageLift.ApplyAsync(
            AsAgentSees(ServerResult(1)), store, Conversation, CallId, CancellationToken.None);
        var flattened = QualifiedMcpTool.Flatten(result);
        var message = Reloaded(new ChatMessage(
            ChatRole.Tool, [new FunctionResultContent(CallId, flattened)]));

        var expanded = await ExpandAsync(message, store);

        var contents = (IList<AIContent>)((FunctionResultContent)expanded.Contents[0]).Result!;
        contents.OfType<TextContent>().First().Text.ShouldBe(FlattenedTextOf(flattened));
        contents.OfType<DataContent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AOneBlockListResult_RewritesWithTheBlocksTextNotItsContentJson()
    {
        // Flatten leaves a single text block as the list it arrived in; the rewrite must read it
        // the way Flatten would have written it, not serialize the AIContent list raw.
        var store = new RecordingStore();
        object result = new AIContent[] { new DataContent(new byte[] { 4, 5 }, "image/png") };
        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, CallId, CancellationToken.None);
        var flattened = QualifiedMcpTool.Flatten(lifted);
        var message = new ChatMessage(
            ChatRole.Tool, [new FunctionResultContent(CallId, flattened)]);

        var expanded = await ExpandAsync(message, store);

        var contents = (IList<AIContent>)((FunctionResultContent)expanded.Contents[0]).Result!;
        contents.OfType<TextContent>().First().Text.ShouldBe(FlattenedTextOf(flattened));
        contents.OfType<DataContent>().ShouldHaveSingleItem();
    }

    // The plain JsonSerializer round trip RedisThreadStateStore puts every message through.
    private static ChatMessage Reloaded(ChatMessage message) =>
        System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(
            System.Text.Json.JsonSerializer.Serialize(message))!;

    private static string FlattenedTextOf(object? flattened) =>
        flattened as string
        ?? string.Join("\n\n", ((IList<AIContent>)flattened!).OfType<TextContent>().Select(c => c.Text));

    private static async Task<ChatMessage> ExpandAsync(ChatMessage message, RecordingStore store) =>
        await ReadImageHydration.ExpandAsync(
            message,
            ReadImageHydration.Reads(message),
            new ReadImageContext(store, Conversation),
            withinDepth: true,
            CancellationToken.None);

    // The store keys hydration derives from the message, which is the whole contract under test.
    private static IReadOnlyList<string> KeysHydrationWillAskFor(ChatMessage message) =>
        ReadImageHydration.Reads(message)
            .Select(read => McpImageLift.KeyFor(read.CallId, read.Index))
            .ToList();

    private static List<ContentBlock> ServerResult(int pictures)
    {
        var images = Enumerable.Range(1, pictures)
            .Select(i => new ViewedImage(
                $"i-{i}",
                "image/jpeg",
                [1, 2, 3],
                FsResultContract.ToNode(new PageImageResult
                {
                    ImageRef = $"i-{i}",
                    Label = $"Picture {i}",
                    MediaType = "image/jpeg",
                    SizeBytes = 3,
                    Shown = true
                })))
            .ToList();

        return ToolResponse.Create(
                FsResultContract.ToNode(new { status = "success", imageCount = pictures }),
                images)
            .Content.ToList();
    }

    // The protocol blocks a server returns, as the MCP client hands them to the bridge.
    private static object AsAgentSees(IReadOnlyList<ContentBlock> blocks) =>
        blocks
            .Select(block => (AIContent)(block switch
            {
                TextContentBlock text => new TextContent(text.Text),
                ImageContentBlock image => new DataContent(image.Data, image.MimeType),
                _ => new TextContent("")
            }))
            .ToArray();

    private static ContentBlock Text(string text) => new TextContentBlock { Text = text };

    private static ContentBlock Image() =>
        new ImageContentBlock { MimeType = "image/png", Data = new byte[] { 4, 5 } };

    private static string ImageEnvelope(string label, bool shown) =>
        FsResultContract.ToNode(new PageImageResult
        {
            ImageRef = "i-1",
            Label = label,
            MediaType = "image/png",
            Shown = shown
        }).ToJsonString();

    private sealed class RecordingStore : IReadImageStore
    {
        private readonly Dictionary<string, ReadImage> _images = [];

        public List<string> Keys { get; } = [];

        public Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct)
        {
            Keys.Add(callId);
            _images[callId] = image;
            return Task.CompletedTask;
        }

        public Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.FromResult(_images.GetValueOrDefault(callId));

        public Task DeleteAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}