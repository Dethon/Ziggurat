using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// The seam where an MCP image becomes a read image. The browse server answers with a protocol
// image block and knows nothing of Redis, conversation ids or tool call ids; the bridge takes the
// bytes out, puts them in the agent's own store under the key hydration will look them up by, and
// leaves the envelope text standing in their place.
//
// Every future MCP server returning an image inherits this without asking for it.
public class McpImageLiftTests
{
    private const string Conversation = "conv-1";
    private const string CallId = "call-7";
    private static readonly byte[] _bytes = [9, 8, 7, 6];

    [Fact]
    public async Task AnImageBlock_HasItsBytesMovedIntoTheStore()
    {
        var store = new RecordingReadImageStore();

        await McpImageLift.ApplyAsync(ResultWithImage(), store, Conversation, CallId, CancellationToken.None);

        var written = store.Written.ShouldHaveSingleItem();
        written.ConversationId.ShouldBe(Conversation);
        // One call can answer with several pictures, so the index separates them under the call id
        // that keys them all rather than widening the store's one-image-per-key contract.
        written.CallId.ShouldBe(McpImageLift.KeyFor(CallId, 0));
        written.Image.Bytes.ShouldBe(_bytes);
        written.Image.MediaType.ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task WhatTheBridgeReturns_CarriesTheEnvelopeTextAndNoBytes()
    {
        var store = new RecordingReadImageStore();

        var lifted = await McpImageLift.ApplyAsync(
            ResultWithImage(), store, Conversation, CallId, CancellationToken.None);

        AllBytes(lifted).ShouldBeEmpty();
        Text(lifted).ShouldContain("A harbour at dusk");
    }

    [Fact]
    public async Task TheStoredImage_IsFoundByTheSameKeyHydrationAsksFor()
    {
        var store = new RecordingReadImageStore();

        var lifted = await McpImageLift.ApplyAsync(
            ResultWithImage(), store, Conversation, CallId, CancellationToken.None);

        // The envelope must parse as a page image, or the send would never come looking.
        PageImageResult.TryRead(System.Text.Json.Nodes.JsonNode.Parse(Text(lifted)))
            .ShouldNotBeNull().Shown.ShouldBeTrue();
        (await store.GetAsync(Conversation, McpImageLift.KeyFor(CallId, 0), CancellationToken.None))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task AHostWithNoStore_PassesTheResultThroughUnchanged()
    {
        // Optional exactly as the attachment source is: a deployment that wants none of this keeps
        // working rather than failing.
        var result = ResultWithImage();

        var lifted = await McpImageLift.ApplyAsync(result, null, Conversation, CallId, CancellationToken.None);

        lifted.ShouldBeSameAs(result);
    }

    [Fact]
    public async Task ATurnWithNoConversationContext_PassesTheResultThroughUnchanged()
    {
        // Nothing to key the bytes under, so storing them would strand them where no send looks.
        var store = new RecordingReadImageStore();
        var result = ResultWithImage();

        var lifted = await McpImageLift.ApplyAsync(result, store, null, CallId, CancellationToken.None);

        lifted.ShouldBeSameAs(result);
        store.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATurnWithNoCallId_PassesTheResultThroughUnchanged()
    {
        var store = new RecordingReadImageStore();
        var result = ResultWithImage();

        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, null, CancellationToken.None);

        lifted.ShouldBeSameAs(result);
        store.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAllTextResult_IsLeftForFlattenAndStoresNothing()
    {
        var store = new RecordingReadImageStore();
        object result = new AIContent[] { new TextContent("one"), new TextContent("two") };

        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, CallId, CancellationToken.None);

        lifted.ShouldBeSameAs(result);
        store.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task APlainStringResult_IsLeftAloneEntirely()
    {
        var store = new RecordingReadImageStore();
        object result = "just a string";

        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, CallId, CancellationToken.None);

        lifted.ShouldBeSameAs(result);
    }

    [Fact]
    public async Task SeveralImagesInOneResult_AllReachTheStore()
    {
        // view_image takes a list, so one call can answer with several pictures. They share the
        // call id, which is the key -- so they must be stored as one entry the send hydrates whole.
        var store = new RecordingReadImageStore();
        object result = new AIContent[]
        {
            new TextContent(EnvelopeText("First picture")),
            new DataContent(_bytes, "image/jpeg"),
            new TextContent(EnvelopeText("Second picture")),
            new DataContent(new byte[] { 1, 1 }, "image/png")
        };

        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, CallId, CancellationToken.None);

        AllBytes(lifted).ShouldBeEmpty();
        store.Written.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task TheRealResultShape_KeysEachPictureWhereHydrationLooksForIt()
    {
        // What ToolResponse.Create actually emits: the call's own envelope first, then each
        // picture's envelope followed by its bytes. Every earlier test in this file started at an
        // image envelope, which is why the call-level one was free to shift the indexes.
        var store = new RecordingReadImageStore();
        object result = new AIContent[]
        {
            new TextContent("""{"status":"success","sessionId":"s","imageCount":2}"""),
            new TextContent(EnvelopeText("First picture")),
            new DataContent(_bytes, "image/jpeg"),
            new TextContent(EnvelopeText("Second picture")),
            new DataContent(new byte[] { 1, 1 }, "image/png")
        };

        var lifted = await McpImageLift.ApplyAsync(result, store, Conversation, CallId, CancellationToken.None);

        // The keys the bridge wrote must be the keys hydration will ask for: the nth page-image
        // envelope in the text pairs with the nth picture.
        var text = Text(lifted);
        var envelopeOrder = text
            .Split("\n")
            .Select(line => line.Trim())
            .Where(line => line.StartsWith('{'))
            .Select(line =>
            {
                try
                {
                    return PageImageResult.TryRead(System.Text.Json.Nodes.JsonNode.Parse(line));
                }
                catch (System.Text.Json.JsonException)
                {
                    return null;
                }
            })
            .ToList();

        var shownIndexes = envelopeOrder
            .Select((envelope, index) => (envelope, index))
            .Where(e => e.envelope is { Shown: true })
            .Select(e => e.index)
            .ToList();

        foreach (var (position, storeIndex) in shownIndexes.Select((i, n) => (i, n)))
        {
            store.Written[storeIndex].CallId.ShouldBe(McpImageLift.KeyFor(CallId, position));
        }
    }

    private static object ResultWithImage() => new AIContent[]
    {
        new TextContent(EnvelopeText("A harbour at dusk")),
        new DataContent(_bytes, "image/jpeg")
    };

    private static string EnvelopeText(string label) =>
        FsResultContract.ToNode(new PageImageResult
        {
            ImageRef = "i-1",
            Label = label,
            MediaType = "image/jpeg",
            SizeBytes = 4,
            Shown = true
        }).ToJsonString();

    private static IReadOnlyList<DataContent> AllBytes(object? result) =>
        result is IList<AIContent> contents ? contents.OfType<DataContent>().ToList() : [];

    private static string Text(object? result) => result switch
    {
        string s => s,
        IList<AIContent> contents => string.Join("\n", contents.OfType<TextContent>().Select(c => c.Text)),
        _ => ""
    };

    private sealed class RecordingReadImageStore : IReadImageStore
    {
        private readonly Dictionary<string, ReadImage> _entries = [];

        public List<(string ConversationId, string CallId, ReadImage Image)> Written { get; } = [];

        public Task PutAsync(string conversationId, string callId, ReadImage image, CancellationToken ct)
        {
            Written.Add((conversationId, callId, image));
            _entries[$"{conversationId}:{callId}"] = image;
            return Task.CompletedTask;
        }

        public Task<ReadImage?> GetAsync(string conversationId, string callId, CancellationToken ct) =>
            Task.FromResult(_entries.GetValueOrDefault($"{conversationId}:{callId}"));

        public Task DeleteAsync(string conversationId, string callId, CancellationToken ct)
        {
            _entries.Remove($"{conversationId}:{callId}");
            return Task.CompletedTask;
        }
    }
}