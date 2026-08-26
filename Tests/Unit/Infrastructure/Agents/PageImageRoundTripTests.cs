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
        public List<string> Keys { get; } = [];

        public Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct)
        {
            Keys.Add(callId);
            return Task.CompletedTask;
        }

        public Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.FromResult<ReadImage?>(null);

        public Task DeleteAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}