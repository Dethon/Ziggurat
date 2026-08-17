using Domain.DTOs.FileSystem;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

// The other half of hydration: putting bytes back where a reference sits, when the reference was put
// there by the model rather than by a person. It runs in the same pass, on the same message list, for
// the same reason — one distance rule, one placeholder shape.
//
// An image cannot travel on the tool message that answered the read: that role's content is a plain
// string on this provider, and content parts are accepted only on a user message. So the bytes arrive
// as their own user message straight after the *whole* tool-result message — never between its
// results, because the function-invoking client puts every result of an iteration in one message and
// some providers reject a conversation in which a tool call was not answered before anything else
// appeared. See docs/adr/0029.
internal static class ReadImageHydration
{
    private const string SystemSender = "system";

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

    public static async Task<ChatMessage?> ExpandAsync(
        IReadOnlyList<ReadImageReference> reads,
        ReadImageContext context,
        bool withinDepth,
        CancellationToken ct)
    {
        if (reads.Count == 0)
        {
            return null;
        }

        var contents = new List<AIContent>();
        foreach (var read in reads)
        {
            var image = withinDepth ? await context.FetchAsync(read.CallId, ct) : null;

            if (image is null)
            {
                // The send an image drops out of view is the send its bytes go. That makes the
                // message window the real bound and the store's own horizon only a backstop for a
                // conversation that went quiet.
                if (!withinDepth)
                {
                    await context.ForgetAsync(read.CallId, ct);
                }

                contents.Add(new TextContent(Placeholder(read.VirtualPath)));
                continue;
            }

            contents.Add(new TextContent(Label(image.VirtualPath)));
            contents.Add(new DataContent(image.Bytes, image.MediaType));
        }

        var injected = new ChatMessage(ChatRole.User, contents);
        injected.SetSenderId(SystemSender);
        var decorated = TurnDecoration.Apply(injected, context.LocalTimeZone);
        decorated.MarkInjected();
        return decorated;
    }

    private static string Label(string virtualPath) =>
        $"[The image you read from {virtualPath}:]";

    // Honest in a way a lost attachment's placeholder cannot be: the file never left the mount, so
    // reading it again really does bring it back. One answer for every way the bytes can be missing —
    // out of depth, expired, evicted, or a host with no store at all.
    private static string Placeholder(string virtualPath) =>
        $"[The image you read from {virtualPath} is no longer in view. Read the file again if you "
        + "still need to look at it — it is still on the mount. Don't describe what it contained "
        + "from memory.]";
}

// One image a tool result promised the model, and the path to name if the bytes cannot be found.
internal sealed record ReadImageReference(string CallId, string VirtualPath);
