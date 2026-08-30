using System.Text.Json;
using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The page's fetch script answers in one string, and this is the C# side reading it. The two
// halves used to meet on a hand-delimited "mediaType|label|base64" -- so an alt text carrying a
// pipe ("Photo | Getty Images") shifted its own tail into the base64 field, the decode threw, and
// the picture came back as the site refusing. The strings the script carries are somebody else's
// text: the payload shape may not have characters they can also have.
public class ImageFetchPayloadTests
{
    private static readonly byte[] _bytes = [1, 2, 3, 4];

    [Fact]
    public void TheLadderComputedLabel_RidesTheParsedAnswer()
    {
        // The label no longer travels in the payload at all: the script answers facts, the one
        // C# ladder names the picture, and the parse attaches that name to the bytes.
        var fetched = ImageFetchPayload.Parse("i-1", Payload("image/jpeg"), "Photo | Getty Images");

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.Label.ShouldBe("Photo | Getty Images");
        fetched.Bytes.ShouldBe(_bytes);
        fetched.MediaType.ShouldBe("image/jpeg");
    }

    [Fact]
    public void TheFactsAnswer_CarriesEveryStringThePageOffers()
    {
        var facts = ImageFetchPayload.Facts(
            """
            {"alt":"Alt","caption":"Cap","title":"Title","linkText":"Link","src":"/p.jpg","w":640,"h":480}
            """);

        facts.ShouldBe(new ImageLabelFacts(
            "Alt", "Cap", "Title", "Link", "/p.jpg", 640, 480));
    }

    [Fact]
    public void AGarbledFactsAnswer_ReadsAsAPageWithNothingToSay()
    {
        var facts = ImageFetchPayload.Facts("not json at all");

        facts.ShouldBe(new ImageLabelFacts(null, null, null, null, null, 0, 0));
    }

    [Fact]
    public void AMissingImage_IsNotAnImageRef()
    {
        ImageFetchPayload.Parse("i-9", null, null).Status.ShouldBe(ImageFetchStatus.NotAnImageRef);
    }

    [Fact]
    public void TheErrorSentinel_ReadsAsTheSiteRefusing()
    {
        ImageFetchPayload.Parse("i-1", "error", null).Status.ShouldBe(ImageFetchStatus.SiteRefused);
    }

    [Fact]
    public void TheNeverLoadedSentinel_ReadsAsADeadLink()
    {
        // A picture whose bytes never arrived is not the site refusing to share pixels it shows —
        // it is a broken address, and retrying it is wasted work.
        ImageFetchPayload.Parse("i-1", "never-loaded", null).Status.ShouldBe(ImageFetchStatus.NeverLoaded);
    }

    [Fact]
    public void ATaintedPayload_CarriesTheAddressToReFetch()
    {
        // A canvas the browser will show but not let script read is not the end of the fetch: the
        // C# side re-requests the address from outside the page, where CORS has no say, and falls
        // back to an element screenshot of the pixels already on screen.
        ImageFetchPayload
            .Tainted("""{"tainted":true,"url":"https://cdn.test/pic.jpg"}""", out var url)
            .ShouldBeTrue();
        url.ShouldBe("https://cdn.test/pic.jpg");
    }

    [Fact]
    public void ATaintedPayloadWithAnEmptyAddress_CarriesNoAddress()
    {
        ImageFetchPayload.Tainted("""{"tainted":true,"url":""}""", out var url).ShouldBeTrue();
        url.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("error")]
    [InlineData("never-loaded")]
    [InlineData("""{"mediaType":"image/png","data":"AQID"}""")]
    public void AnythingElse_IsNotTainted(string? payload)
    {
        ImageFetchPayload.Tainted(payload, out _).ShouldBeFalse();
    }

    [Fact]
    public void AMalformedPayload_ReadsAsTheSiteRefusing()
    {
        // The old delimited shape, and any other string the script never wrote: a refusal, not a
        // throw out of the fetch loop.
        ImageFetchPayload.Parse("i-1", "image/png|A label|%%not-base64%%", null)
            .Status.ShouldBe(ImageFetchStatus.SiteRefused);
    }

    [Fact]
    public void APayloadWithoutItsBytes_ReadsAsTheSiteRefusing()
    {
        ImageFetchPayload.Parse("i-1", """{"mediaType":"image/png"}""", null)
            .Status.ShouldBe(ImageFetchStatus.SiteRefused);
    }

    private static string Payload(string mediaType) =>
        JsonSerializer.Serialize(new
        {
            mediaType,
            data = Convert.ToBase64String(_bytes)
        });
}