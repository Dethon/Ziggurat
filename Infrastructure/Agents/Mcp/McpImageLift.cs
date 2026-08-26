using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.Mcp;

// Where an image returned by an MCP server becomes a read image. The server answers what the
// protocol says an image result is and learns nothing about Redis, conversation ids or tool call
// ids; this takes the bytes out, writes them to the agent's own store under the key hydration will
// look them up by, and leaves the envelope text standing in their place.
//
// Nothing downstream of here ever sees the bytes, so a turn is serialized into the history as a
// list of text — which is the whole point. A DataContent left in place would serialize as base64
// and cost its full weight on every subsequent turn, forever, for a picture the model can no
// longer see.
//
// Every future MCP server that returns an image inherits eviction and hydration without opting in.
internal static class McpImageLift
{
    // Several pictures can answer one call — view_image takes a list — and they share the call id
    // that keys the store. The index separates them without widening the store's contract, which
    // file_read relies on being one image per key.
    public const char IndexSeparator = '#';

    public static string KeyFor(string callId, int index) => $"{callId}{IndexSeparator}{index}";

    public static async Task<object?> ApplyAsync(
        object? result,
        IReadImageStore? store,
        string? conversationId,
        string? callId,
        CancellationToken ct)
    {
        // Three ways this host cannot key bytes: no store at all, a run carrying no conversation
        // context, a result outside a tool call. Each passes the result through as today rather
        // than storing bytes under a key no send will ever ask for.
        if (store is null || string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(callId))
        {
            return result;
        }

        if (result is not IList<AIContent> contents || !contents.Any(c => c is DataContent))
        {
            return result;
        }

        var kept = new List<AIContent>(contents.Count);

        // Both sides count the same thing: a picture's index is the position of the envelope that
        // introduces it among all the page-image envelopes in the result. Counting pictures here
        // and envelopes in hydration is what let the call's own envelope shift every key by one.
        var envelopesSeen = 0;

        // The envelope waiting for its picture. Consumed when one arrives: an envelope introduces
        // exactly one image, so a second image behind the same envelope must not share its key.
        (int Index, string Label)? pending = null;

        // A loop because each block is read against state the blocks before it left -- the running
        // envelope count and the envelope still waiting for its picture. A projection would need
        // that state threaded through it anyway.
        foreach (var content in contents)
        {
            if (content is TextContent text)
            {
                // Counted paragraph by paragraph, not block by block: Flatten joins text blocks
                // with a blank line and hydration splits the joined text on it, so a '{'-paragraph
                // embedded inside one block is a candidate over there whether or not it was ever
                // its own block. Counting whole blocks here shifted every key after such a
                // paragraph by one.
                foreach (var part in EnvelopeCandidates(text.Text))
                {
                    if (LabelOf(part) is { } label)
                    {
                        pending = (envelopesSeen, label);
                    }

                    envelopesSeen++;
                }

                kept.Add(content);
                continue;
            }

            if (content is not DataContent image)
            {
                kept.Add(content);
                continue;
            }

            // An image nothing announced -- a server that never learned view_image's envelope
            // shape -- gets one written here. Bytes with no envelope are bytes hydration can never
            // find: the model would neither see the picture nor be told anything stood in its
            // place, which is quieter than either passing it through or refusing it.
            if (pending is null)
            {
                kept.Add(new TextContent(SynthesizedEnvelope(image)));
                pending = (envelopesSeen, UnannouncedLabel);
                envelopesSeen++;
            }

            await store.PutAsync(
                conversationId,
                KeyFor(callId, pending.Value.Index),
                new ReadImage
                {
                    // The label the entry gave this picture, taken off the envelope that precedes
                    // it, so a note left in its place names what the model actually asked for.
                    VirtualPath = pending.Value.Label,
                    MediaType = image.MediaType,
                    Bytes = image.Data.ToArray()
                },
                ct);
            pending = null;
        }

        return kept;
    }

    private const string UnannouncedLabel = "an image the tool returned";

    // The same shape view_image writes, so hydration recognises it without knowing it was the
    // bridge speaking: the block position the bytes were stored under is the position this
    // envelope occupies among the result's JSON blocks.
    private static string SynthesizedEnvelope(DataContent image) =>
        FsResultContract.ToNode(new PageImageResult
        {
            ImageRef = "(unlisted)",
            Label = UnannouncedLabel,
            MediaType = image.MediaType,
            SizeBytes = image.Data.Length,
            Shown = true
        }).ToJsonString();

    // The envelope sits immediately before its picture, which is the order view_image writes them
    // in and the order the model reads them. A block that is not a page-image envelope yields no
    // label -- the note then says "a page image", which is worse than a name and better than a
    // failed send.
    private static string? LabelOf(string text) => Parse(text)?.Label;

    // Anything hydration's own split will count as a candidate. It has to be the same question,
    // asked the same way, or the two sides number the pictures differently again.
    public static IEnumerable<string> EnvelopeCandidates(string text) =>
        text.Split("\n\n").Where(part => part.TrimStart().StartsWith('{'));

    private static PageImageResult? Parse(string text)
    {
        try
        {
            return PageImageResult.TryRead(JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}