using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Utils;

public static class ToolResponse
{
    // Typed filesystem results carry their own success/error discriminator; ToNode() renders the
    // success payload (camelCase, nulls omitted) or the ok:false error envelope, and Create(JsonNode)
    // propagates that to MCP's IsError flag — so an Err surfaces as a protocol-level error.
    public static CallToolResult Create<T>(FsResult<T> result) where T : class => Create(result.ToNode());

    public static CallToolResult Create(Exception ex)
    {
        var envelope = ToolError.Create(MapErrorCode(ex), ex.Message);

        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = envelope.ToJsonString()
                }
            ]
        };
    }

    // Inspects the envelope so an `ok:false` payload propagates to MCP's IsError flag.
    // Previously this method always set IsError=false; the change lets envelope-shaped
    // failures from Domain tools surface at the MCP protocol level (and through any
    // downstream consumer that branches on IsError) without a separate signal channel.
    public static CallToolResult Create(JsonNode json)
    {
        return new CallToolResult
        {
            IsError = ToolErrorResult.IsErrorEnvelope(json),
            Content =
            [
                new TextContentBlock
                {
                    Text = json.ToJsonString()
                }
            ]
        };
    }

    public static CallToolResult Create(JsonNode envelope, params string?[] bodies)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = envelope.ToJsonString() } };
        content.AddRange(bodies
            .Where(b => b is not null)
            .Select(b => (ContentBlock)new TextContentBlock { Text = b! }));

        return new CallToolResult
        {
            IsError = ToolErrorResult.IsErrorEnvelope(envelope),
            Content = content
        };
    }

    // An image result: the call's own envelope, then each picture preceded by the envelope that
    // says which image it is. The server hands back what the protocol says an image result is and
    // knows nothing of where the bytes end up -- QualifiedMcpTool lifts them out on the way in.
    public static CallToolResult Create(JsonNode envelope, IReadOnlyList<(JsonNode Envelope, string MediaType, byte[] Bytes)> images)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = envelope.ToJsonString() } };

        foreach (var (imageEnvelope, mediaType, bytes) in images)
        {
            content.Add(new TextContentBlock { Text = imageEnvelope.ToJsonString() });
            content.Add(new ImageContentBlock
            {
                MimeType = mediaType,
                Data = bytes
            });
        }

        return new CallToolResult
        {
            IsError = ToolErrorResult.IsErrorEnvelope(envelope),
            Content = content
        };
    }

    public static CallToolResult Create(string message)
    {
        return new CallToolResult
        {
            IsError = false,
            Content =
            [
                new TextContentBlock
                {
                    Text = message
                }
            ]
        };
    }

    // The mapping itself lives in Domain (ToolError.CodeFor), because a channel server's filter
    // needs the same answer and cannot reach Infrastructure.
    private static string MapErrorCode(Exception ex) => ToolError.CodeFor(ex);
}