using System.Text.Json;
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
                .Select(result => (result.CallId, Envelope: FsImageReadResult.TryRead(result.Result)))
                .Where(read => read.Envelope is { Shown: true })
                .Select(read => new ReadImageReference(read.CallId, read.Envelope!.FilePath))
                .ToList();

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
        var byCallId = reads.ToDictionary(r => r.CallId);

        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            if (content is not FunctionResultContent result ||
                !byCallId.TryGetValue(result.CallId, out var read))
            {
                contents.Add(content);
                continue;
            }

            contents.Add(new FunctionResultContent(
                result.CallId, await RewriteAsync(result.Result, read, context, withinDepth, ct)));
        }

        var copy = message.Clone();
        copy.Contents = contents;
        return copy;
    }

    private static async Task<object?> RewriteAsync(
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
            await context.ForgetAsync(read.CallId, ct);
            return $"{EnvelopeText(envelope)}\n{Placeholder(read.VirtualPath)}";
        }

        // The wire rejects a whole request carrying an image the model cannot take, rather than
        // stripping it — and a later turn may be back on a model that sees, so the bytes stay.
        if (!context.ModelAcceptsImages)
        {
            return $"{EnvelopeText(envelope)}\n{NoVisionPlaceholder(read.VirtualPath)}";
        }

        var image = await context.FetchAsync(read.CallId, ct);
        if (image is null)
        {
            return $"{EnvelopeText(envelope)}\n{Placeholder(read.VirtualPath)}";
        }

        return new List<AIContent>
        {
            new TextContent(EnvelopeText(envelope)),
            new DataContent(image.Bytes, image.MediaType)
        };
    }

    private static string EnvelopeText(object? envelope) =>
        envelope as string ?? JsonSerializer.Serialize(envelope);

    // Honest in a way a lost attachment's placeholder cannot be: the file never left the mount, so
    // reading it again really does bring it back. One answer for every way the bytes can be missing —
    // out of depth, expired, evicted, or a host with no store at all.
    private static string Placeholder(string virtualPath) =>
        $"[The image you read from {virtualPath} is no longer in view. Read the file again if you "
        + "still need to look at it — it is still on the mount. Don't describe what it contained "
        + "from memory.]";

    private static string NoVisionPlaceholder(string virtualPath) =>
        $"[The image you read from {virtualPath} was not shown: the model running this turn does "
        + "not accept images. Don't describe what it contained from memory.]";
}

// One image a tool result promised the model, and the path to name if the bytes cannot be found.
internal sealed record ReadImageReference(string CallId, string VirtualPath);