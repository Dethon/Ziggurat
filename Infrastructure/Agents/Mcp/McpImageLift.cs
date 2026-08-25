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
        var stored = 0;

        foreach (var content in contents)
        {
            if (content is not DataContent image)
            {
                kept.Add(content);
                continue;
            }

            await store.PutAsync(
                conversationId,
                KeyFor(callId, stored),
                new ReadImage
                {
                    // The label the entry gave this picture, taken off the envelope that precedes
                    // it, so a note left in its place names what the model actually asked for.
                    VirtualPath = LabelBefore(kept) ?? "a page image",
                    MediaType = image.MediaType,
                    Bytes = image.Data.ToArray()
                },
                ct);

            stored++;
        }

        return kept;
    }

    // The envelope sits immediately before its picture, which is the order view_image writes them
    // in and the order the model reads them. A block that is not an envelope at all just yields no
    // label -- the note then says "a page image", which is worse than a name and better than a
    // failed send.
    private static string? LabelBefore(IReadOnlyList<AIContent> written)
    {
        if (written.OfType<TextContent>().LastOrDefault() is not { } text)
        {
            return null;
        }

        try
        {
            return PageImageResult.TryRead(JsonNode.Parse(text.Text))?.Label;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}