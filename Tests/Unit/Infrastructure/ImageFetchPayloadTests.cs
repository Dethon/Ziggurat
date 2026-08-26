using System.Text.Json;
using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The page's fetch script answers in one string, and this is the C# side reading it. The two
// halves used to meet on a hand-delimited "mediaType|label|base64" -- so an alt text carrying a
// pipe ("Photo | Getty Images") shifted its own tail into the base64 field, the decode threw, and
// the picture came back as the site refusing. A label is somebody else's text: the payload shape
// may not have characters a label can also have.
public class ImageFetchPayloadTests
{
    private static readonly byte[] _bytes = [1, 2, 3, 4];

    [Fact]
    public void ALabelCarryingAPipe_DoesNotCorruptTheBytes()
    {
        var fetched = ImageFetchPayload.Parse("i-1", Payload("image/jpeg", "Photo | Getty Images"));

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.Label.ShouldBe("Photo | Getty Images");
        fetched.Bytes.ShouldBe(_bytes);
        fetched.MediaType.ShouldBe("image/jpeg");
    }

    [Fact]
    public void AMissingImage_IsNotAnImageRef()
    {
        ImageFetchPayload.Parse("i-9", null).Status.ShouldBe(ImageFetchStatus.NotAnImageRef);
    }

    [Fact]
    public void TheErrorSentinel_ReadsAsTheSiteRefusing()
    {
        ImageFetchPayload.Parse("i-1", "error").Status.ShouldBe(ImageFetchStatus.SiteRefused);
    }

    [Fact]
    public void TheNeverLoadedSentinel_ReadsAsADeadLink()
    {
        // A picture whose bytes never arrived is not the site refusing to share pixels it shows —
        // it is a broken address, and retrying it is wasted work.
        ImageFetchPayload.Parse("i-1", "never-loaded").Status.ShouldBe(ImageFetchStatus.NeverLoaded);
    }

    [Fact]
    public void AMalformedPayload_ReadsAsTheSiteRefusing()
    {
        // The old delimited shape, and any other string the script never wrote: a refusal, not a
        // throw out of the fetch loop.
        ImageFetchPayload.Parse("i-1", "image/png|A label|%%not-base64%%")
            .Status.ShouldBe(ImageFetchStatus.SiteRefused);
    }

    [Fact]
    public void APayloadWithoutItsBytes_ReadsAsTheSiteRefusing()
    {
        ImageFetchPayload.Parse("i-1", """{"mediaType":"image/png","label":"A label"}""")
            .Status.ShouldBe(ImageFetchStatus.SiteRefused);
    }

    [Fact]
    public void AnEmptyLabel_ComesBackAsNoLabel()
    {
        ImageFetchPayload.Parse("i-1", Payload("image/png", "")).Label.ShouldBeNull();
    }

    private static string Payload(string mediaType, string label) =>
        JsonSerializer.Serialize(new
        {
            mediaType,
            label,
            data = Convert.ToBase64String(_bytes)
        });
}