using System.Text.Json;
using Domain.Tools.Web;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

// What a call acting as the person is approved for: the tool on one site. The arguments arrive as
// the model wrote them (JSON) or as an in-process caller handed them over (CLR values).
public class AsPersonSubjectTests
{
    private const string Browse = "mcp__mcp-websearch__web_browse";

    private static Dictionary<string, object?> Args(object? asMe, string? url = "https://mail.google.com/mail/u/0") =>
        new() { ["url"] = url, ["asMe"] = asMe };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ACallAsThePerson_IsTheToolOnTheSitesRegistrableDomain()
    {
        AsPersonSubject.Of(Browse, Args(true)).ShouldBe(new AsPersonSubject(Browse, "google.com"));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"true\"")]
    [InlineData("\"True\"")]
    public void AsMeWrittenAsJson_IsRead(string json)
    {
        AsPersonSubject.Of(Browse, Args(Json(json)))!.Site.ShouldBe("google.com");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void ACallNotAsThePerson_HasNoSubject(bool? asMe)
    {
        AsPersonSubject.Of(Browse, Args(asMe)).ShouldBeNull();
    }

    [Fact]
    public void ACallWithNoAsMe_HasNoSubject()
    {
        AsPersonSubject.Of(Browse, new Dictionary<string, object?> { ["url"] = "https://shop.com" }).ShouldBeNull();
        AsPersonSubject.Of(Browse, Args(Json("false"))).ShouldBeNull();
    }

    // Still a call as the person, so still asked — about the tool alone, since there is no site to
    // scope a remembered approval to.
    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void ACallAsThePersonWithNoParseableUrl_IsTheToolAlone(string? url)
    {
        AsPersonSubject.Of(Browse, Args(true, url)).ShouldBe(new AsPersonSubject(Browse, null));
    }

    [Fact]
    public void TwoHostsOfOneSite_AreOneSubject_AndTwoSitesAreTwo()
    {
        AsPersonSubject.Of(Browse, Args(true, "https://www.shop.com/orders"))
            .ShouldBe(AsPersonSubject.Of(Browse, Args(true, "https://shop.com/")));
        AsPersonSubject.Of(Browse, Args(true, "https://shop.com/"))
            .ShouldNotBe(AsPersonSubject.Of(Browse, Args(true)));
    }
}