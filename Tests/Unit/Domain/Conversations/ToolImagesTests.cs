using System.Text.Json;
using Domain.Conversations;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

public class ToolImagesTests
{
    private static JsonElement Envelope(object envelope) => JsonSerializer.SerializeToElement(envelope, FsResultContract.SerializerOptions);

    // A file read is one picture, keyed by its call alone, and named by its own file.
    [Fact]
    public void AnImageRead_IsOnePicture_KeptUnderTheConversationsDirectory()
    {
        var result = new FunctionResultContent("call_1", Envelope(new FsImageReadResult
        {
            FilePath = "/vault/photos/cat.png", MediaType = "image/png", SizeBytes = 1234, Shown = true
        }));

        var shown = ToolImages.Of(result, "42:7").ShouldHaveSingleItem();

        shown.StoreKey.ShouldBe("call_1");
        shown.Reference.Id.ShouldBe("42-7/tool-call_1");
        (shown.Reference.FileName, shown.Reference.MediaType, shown.Reference.SizeBytes).ShouldBe(("cat.png", "image/png", 1234));
    }

    // A view_image answer names several pictures after its own envelope, each keyed by its place, as the
    // bridge stored them; a refused one takes its place and shows nothing.
    [Fact]
    public void APagesPictures_AreEachKeyedByTheirPlace()
    {
        var text = string.Join("\n\n",
            """{"images":2}""",
            JsonSerializer.Serialize(new PageImageResult { ImageRef = "i-1", Label = "A red bicycle", MediaType = "image/jpeg", Shown = true }, FsResultContract.SerializerOptions),
            JsonSerializer.Serialize(new PageImageResult { ImageRef = "i-2", Label = "gone", MediaType = "image/jpeg", Shown = false, Note = "refused" }, FsResultContract.SerializerOptions),
            JsonSerializer.Serialize(new PageImageResult { ImageRef = "i-3", Label = "", MediaType = "image/webp", Shown = true }, FsResultContract.SerializerOptions));

        var shown = ToolImages.Of(new FunctionResultContent("c9", text), "42:7");

        shown.Select(s => (s.StoreKey, s.Reference.Id, s.Reference.FileName)).ShouldBe(
        [
            ("c9#1", "42-7/tool-c9-1", "A red bicycle.jpg"),
            ("c9#3", "42-7/tool-c9-3", "image.webp")
        ]);
    }

    [Fact]
    public void AnAnswerThatShowedNoPicture_HasNone()
    {
        ToolImages.Of(new FunctionResultContent("c1", "plain words"), "42:7").ShouldBeEmpty();
        ToolImages.Of(new FunctionResultContent("c1", Envelope(new FsImageReadResult
        {
            FilePath = "/a.png", MediaType = "image/png", Shown = false, Note = "too large"
        })), "42:7").ShouldBeEmpty();
    }
}