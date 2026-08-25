using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

// The other half of hydration: putting bytes back where a reference sits, when the reference was put
// there by the model rather than by a person. It runs in the same pass, on the same message list, for
// the same reason — one distance rule, one placeholder shape.
//
// The bytes travel inside the tool result that answered the read: the Responses wire accepts content
// parts in a function call output, so the envelope is followed by the picture itself, in the one
// place the model already looks for the answer. Nothing else is added to the conversation, so there
// is no injected message to attribute, place, or exclude from anything. See docs/adr/0029.
internal static class ReadImageHydration
{
    // Which tool results in this message promised the model a picture. Recognised by the envelope's
    // own shape, which parses strictly, so no other tool's result can be mistaken for an image read
    // and no second message has to be consulted to identify one. An envelope that already said the
    // image was not shown is skipped: the tool refused for a reason that has not changed.
    //
    // Read once per message and handed on, because parsing it means serializing every tool result in
    // the message — a cost every send would otherwise pay again for every tool call in the history.
    public static IReadOnlyList<ReadImageReference> Reads(ChatMessage message) =>
        message.Role != ChatRole.Tool
            ? []
            : message.Contents
                .OfType<FunctionResultContent>()
                .SelectMany(result => Recognise(result.CallId, result.Result))
                .ToList();

    // Two envelopes, each strict about its own shape. Asked in turn rather than merged into one
    // looser parse: the filesystem envelope's strictness is what stops a result that merely carries
    // a path being mistaken for an image read, and a shape that admitted both would spend it.
    private static IEnumerable<ReadImageReference> Recognise(string callId, object? result)
    {
        if (FsImageReadResult.TryRead(result) is { Shown: true } file)
        {
            return [new ReadImageReference(callId, file.FilePath, ReadImageOrigin.Mount)];
        }

        // A page result can carry several envelopes: view_image takes a list, and the bridge joins
        // what it leaves behind into one text. Each envelope is one picture, indexed in the order
        // the bridge stored them.
        return PageImages(callId, result);
    }

    private static IEnumerable<ReadImageReference> PageImages(string callId, object? result)
    {
        var text = result as string ?? (result is JsonNode node ? node.ToJsonString() : null);
        if (text is null)
        {
            return PageImageResult.TryRead(result) is { Shown: true } single
                ? [new ReadImageReference(callId, single.Label, ReadImageOrigin.Page)]
                : [];
        }

        // Every envelope in the joined text, in order, so an index matches the key the bridge
        // wrote under -- including the ones that were refused, which take a slot and no picture.
        return SplitEnvelopes(text)
            .Select((envelope, index) => (Envelope: envelope, Index: index))
            .Where(e => e.Envelope is { Shown: true })
            .Select(e => new ReadImageReference(
                callId, e.Envelope!.Label, ReadImageOrigin.Page, e.Index))
            .ToList();
    }

    private static IEnumerable<PageImageResult?> SplitEnvelopes(string text) =>
        text.Split("\n\n")
            .Select(part => part.Trim())
            .Where(part => part.StartsWith('{'))
            .Select(part =>
            {
                try
                {
                    return PageImageResult.TryRead(JsonNode.Parse(part));
                }
                catch (JsonException)
                {
                    return null;
                }
            });

    // Where a page image's bytes rest. One call can answer with several pictures, so the index
    // within the call joins the call id that keys them all; a mount read is one image per call and
    // keeps the bare id. The bridge writes under the same rule.
    private static string StoreKey(ReadImageReference read) =>
        read.Origin == ReadImageOrigin.Mount ? read.CallId : $"{read.CallId}#{read.Index}";

    // A copy of the tool message whose promised results carry their bytes — or, where the bytes
    // cannot or must not travel, a placeholder appended to the envelope. The original message is
    // never touched: the copy is thrown away with the request, like everything hydration builds.
    public static async Task<ChatMessage> ExpandAsync(
        ChatMessage message,
        IReadOnlyList<ReadImageReference> reads,
        ReadImageContext context,
        bool withinDepth,
        CancellationToken ct)
    {
        // Grouped rather than keyed one-to-one: a single view_image call can promise several
        // pictures, and they all answer under the same call id.
        var byCallId = reads.GroupBy(r => r.CallId).ToDictionary(g => g.Key, g => g.ToList());

        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            if (content is not FunctionResultContent result ||
                !byCallId.TryGetValue(result.CallId, out var group))
            {
                contents.Add(content);
                continue;
            }

            contents.Add(new FunctionResultContent(
                result.CallId, await RewriteAsync(result.Result, group, context, withinDepth, ct)));
        }

        var copy = message.Clone();
        copy.Contents = contents;
        return copy;
    }

    private static async Task<object?> RewriteAsync(
        object? envelope,
        IReadOnlyList<ReadImageReference> reads,
        ReadImageContext context,
        bool withinDepth,
        CancellationToken ct)
    {
        // The common case by far -- a mount read, or a call that asked for one picture -- keeps the
        // single-envelope shape the wire and the truncator already estimate correctly.
        if (reads.Count == 1)
        {
            return await RewriteOneAsync(envelope, reads[0], context, withinDepth, ct);
        }

        var parts = new List<AIContent> { new TextContent(EnvelopeText(envelope)) };
        foreach (var read in reads)
        {
            if (!withinDepth)
            {
                await context.ForgetAsync(StoreKey(read), ct);
                parts.Add(new TextContent(Placeholder(read)));
                continue;
            }

            if (!context.ModelAcceptsImages)
            {
                parts.Add(new TextContent(NoVisionPlaceholder(read)));
                continue;
            }

            var picture = await context.FetchAsync(StoreKey(read), ct);
            parts.Add(picture is null
                ? new TextContent(Placeholder(read))
                : new DataContent(picture.Bytes, picture.MediaType));
        }

        return parts;
    }

    private static async Task<object?> RewriteOneAsync(
        object? envelope,
        ReadImageReference read,
        ReadImageContext context,
        bool withinDepth,
        CancellationToken ct)
    {
        if (!withinDepth)
        {
            // The send an image drops out of view is the send its bytes go. That makes the
            // message window the real bound and the store's own horizon only a backstop for a
            // conversation that went quiet.
            await context.ForgetAsync(StoreKey(read), ct);
            return $"{EnvelopeText(envelope)}\n{Placeholder(read)}";
        }

        // The wire rejects a whole request carrying an image the model cannot take, rather than
        // stripping it — and a later turn may be back on a model that sees, so the bytes stay.
        if (!context.ModelAcceptsImages)
        {
            return $"{EnvelopeText(envelope)}\n{NoVisionPlaceholder(read)}";
        }

        var image = await context.FetchAsync(StoreKey(read), ct);
        if (image is null)
        {
            return $"{EnvelopeText(envelope)}\n{Placeholder(read)}";
        }

        return new List<AIContent>
        {
            new TextContent(EnvelopeText(envelope)),
            new DataContent(image.Bytes, image.MediaType)
        };
    }

    private static string EnvelopeText(object? envelope) =>
        envelope as string ?? JsonSerializer.Serialize(envelope);

    // Honest in a way a lost attachment's placeholder cannot be: what the image was read from
    // outlives the image, so asking again really does bring it back. One answer for every way the
    // bytes can be missing — out of depth, expired, evicted, or a host with no store at all.
    //
    // Where to ask again differs by origin, and that is the whole difference: a mount still holds
    // the file, while a page's refs died with the session that issued them.
    private static string Placeholder(ReadImageReference read) =>
        read.Origin == ReadImageOrigin.Mount
            ? $"[The image you read from {read.Name} is no longer in view. Read the file again if "
              + "you still need to look at it — it is still on the mount. Don't describe what it "
              + "contained from memory.]"
            : $"[The image \"{read.Name}\" is no longer in view. Browse the page again and ask for "
              + "it by its new ref if you still need to look at it. Don't describe what it "
              + "contained from memory.]";

    private static string NoVisionPlaceholder(ReadImageReference read) =>
        $"[The image {Describe(read)} was not shown: the model running this turn does not accept "
        + "images. Don't describe what it contained from memory.]";

    private static string Describe(ReadImageReference read) =>
        read.Origin == ReadImageOrigin.Mount ? $"you read from {read.Name}" : $"\"{read.Name}\"";
}

// Where an image came from, which decides only what its note tells the model to do to see it
// again. Everything else about a read image is the same either way.
internal enum ReadImageOrigin
{
    Mount,
    Page
}

// One image a tool result promised the model, and what to name if the bytes cannot be found — a
// mount path, or the label the page's entry gave the picture.
internal sealed record ReadImageReference(
    string CallId, string Name, ReadImageOrigin Origin, int Index = 0);