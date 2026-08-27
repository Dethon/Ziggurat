using Domain.DTOs.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

// A page image is a read image: the model asked for it, it rides in a tool result, and it can be
// asked for again. What differs is only what to name when the bytes are gone -- a page has no path
// to re-read, so the entry's own label stands in its place.
//
// The hydration behaviour itself (distance, forgetting, no-vision) is pinned at the chat-client
// seam beside the filesystem envelope's, because both envelopes go through one pass. These tests
// pin recognition: the second shape parses, and admitting it does not loosen the first.
public class PageImageHydrationTests
{
    [Fact]
    public void APageImageEnvelope_IsRecognisedAsAnImageRead()
    {
        var node = FsResultContract.ToNode(new PageImageResult
        {
            ImageRef = "i-3",
            Label = "A harbour at dusk",
            MediaType = "image/jpeg",
            SizeBytes = 40_000,
            Shown = true
        });

        var read = PageImageResult.TryRead(node);

        read.ShouldNotBeNull();
        read.ImageRef.ShouldBe("i-3");
        read.Label.ShouldBe("A harbour at dusk");
        read.Shown.ShouldBeTrue();
    }

    [Fact]
    public void AFilesystemEnvelope_DoesNotParseAsAPageImage()
    {
        // Two shapes, each strict about its own. A result carrying a path is a mount read and
        // nothing else -- the reason the filesystem envelope was made strict in the first place.
        var node = FsResultContract.ToNode(new FsImageReadResult
        {
            FilePath = "/vault/shots/error.png",
            MediaType = "image/png",
            SizeBytes = 4,
            Shown = true
        });

        PageImageResult.TryRead(node).ShouldBeNull();
    }

    [Fact]
    public void APageImageEnvelope_DoesNotParseAsAFilesystemRead()
    {
        var node = FsResultContract.ToNode(new PageImageResult
        {
            ImageRef = "i-1",
            Label = "A chart",
            MediaType = "image/png",
            Shown = true
        });

        FsImageReadResult.TryRead(node).ShouldBeNull();
    }

    [Fact]
    public void AResultThatIsNotAnImageReadAtAll_StillParsesAsNeither()
    {
        // The strictness both envelopes rely on: an unrelated tool result that happens to carry
        // some of the same field names is not an image read.
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{"status":"success","sessionId":"abc","url":"http://example.com"}""");

        PageImageResult.TryRead(node).ShouldBeNull();
        FsImageReadResult.TryRead(node).ShouldBeNull();
    }

    [Fact]
    public void APageImageTheToolCouldNotShow_CarriesItsReason()
    {
        var node = FsResultContract.ToNode(new PageImageResult
        {
            ImageRef = "i-2",
            Label = "A product photo",
            MediaType = "image/jpeg",
            Shown = false,
            Note = "The site refused the request for this image."
        });

        var read = PageImageResult.TryRead(node);

        read!.Shown.ShouldBeFalse();
        read.Note.ShouldNotBeNullOrEmpty();
    }
}