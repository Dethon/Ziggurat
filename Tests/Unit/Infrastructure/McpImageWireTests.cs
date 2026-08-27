using System.Text.Json;
using Domain.DTOs.FileSystem;
using Domain.Tools.Web;
using Infrastructure.Utils;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// An image result has to survive the MCP serializer, not merely be constructed. Every other test
// around this feature builds AIContent by hand, on the agent's side of the wire -- so none of them
// could see that the block the server emitted was one the client refused to parse.
//
// It shipped that way: view_image answered IsError=false in 105ms, the call threw at the client
// before the bridge saw a result, no bytes ever reached the store, and the model worked around it
// by downloading the picture into the sandbox and reading it with file_read.
public class McpImageWireTests
{
    [Fact]
    public void AnImageResult_SurvivesTheMcpSerializer()
    {
        var result = ToolResponse.Create(
            FsResultContract.ToNode(new { status = "success", imageCount = 1 }),
            new List<ViewedImage>
            {
                new("i-1", "image/png", [1, 2, 3],
                    FsResultContract.ToNode(new PageImageResult
                    {
                        ImageRef = "i-1", Label = "A picture",
                        MediaType = "image/png", SizeBytes = 3, Shown = true
                    }))
            });

        // Serialize through the type's own contract, the way the server's transport does, rather
        // than through a plain object serialization that misses the polymorphic converter.
        var json = JsonSerializer.Serialize<CallToolResult>(result, McpJsonUtilities.DefaultOptions);
        var back = JsonSerializer.Deserialize<CallToolResult>(json, McpJsonUtilities.DefaultOptions);

        back.ShouldNotBeNull();
        back.Content.Count.ShouldBe(3);

        // The bytes the model will be shown must be the bytes the page served.
        var image = back.Content[2].ShouldBeOfType<ImageContentBlock>();
        image.MimeType.ShouldBe("image/png");
        image.DecodedData.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }
}