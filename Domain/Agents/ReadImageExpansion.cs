using Domain.Contracts;
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
internal static class ReadImageExpansion
{
    private const string SystemSender = "system";

    // Which tool results in this message promised the model a picture. Recognised by the envelope's
    // own shape, which parses strictly, so no other tool's result can be mistaken for an image read
    // and no second message has to be consulted to identify one. An envelope that already said the
    // image was not shown is skipped: the tool refused for a reason that has not changed.
    public static bool Produced(ChatMessage message) => Reads(message).Any();

    public static async Task<ChatMessage?> ExpandAsync(
        ChatMessage toolResults,
        IReadImageStore store,
        string conversationId,
        bool withinDepth,
        TimeZoneInfo localTimeZone,
        CancellationToken ct)
    {
        var contents = new List<AIContent>();
        foreach (var (callId, envelope) in Reads(toolResults))
        {
            var image = withinDepth
                ? await store.GetAsync(conversationId, callId, ct)
                : null;

            if (image is null)
            {
                // The send an image drops out of view is the send its bytes go. That makes the
                // message window the real bound and the store's own horizon only a backstop for a
                // conversation that went quiet.
                if (!withinDepth)
                {
                    await store.DeleteAsync(conversationId, callId, ct);
                }

                contents.Add(new TextContent(Placeholder(envelope.FilePath)));
                continue;
            }

            contents.Add(new TextContent(Label(image.VirtualPath)));
            contents.Add(new DataContent(image.Bytes, image.MediaType));
        }

        if (contents.Count == 0)
        {
            return null;
        }

        var injected = new ChatMessage(ChatRole.User, contents);
        injected.SetSenderId(SystemSender);
        var decorated = TurnDecoration.Apply(injected, localTimeZone);
        decorated.MarkInjected();
        return decorated;
    }

    private static IEnumerable<(string CallId, FsImageReadResult Envelope)> Reads(ChatMessage message) =>
        message.Role != ChatRole.Tool
            ? []
            : message.Contents
                .OfType<FunctionResultContent>()
                .Select(result => (result.CallId, Envelope: FsImageReadResult.TryRead(result.Result)))
                .Where(read => read.Envelope is { Shown: true })
                .Select(read => (read.CallId, read.Envelope!));

    private static string Label(string virtualPath) =>
        $"[The image you read from {virtualPath}:]";

    // Honest in a way a lost attachment's placeholder cannot be: the file never left the mount, so
    // reading it again really does bring it back.
    private static string Placeholder(string virtualPath) =>
        $"[The image you read from {virtualPath} is no longer in view. Read the file again if you "
        + "still need to look at it — it is still on the mount. Don't describe what it contained "
        + "from memory.]";
}