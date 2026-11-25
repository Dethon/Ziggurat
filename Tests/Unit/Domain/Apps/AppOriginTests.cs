using Domain.Apps;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// Which built app a request is for, read from its Host alone: the label under the apps domain is
// `<slug>-<id>` for the published app and `<slug>-<id>-preview` for its working tree, and only the
// id decides, so a renamed app's old links still open it.
public class AppOriginTests
{
    private const string Domain = "apps.example";

    [Theory]
    [InlineData("glucose-today-k3x9p2.apps.example", "k3x9p2", false)]
    [InlineData("glucose-today-k3x9p2-preview.apps.example", "k3x9p2", true)]
    [InlineData("GLUCOSE-TODAY-K3X9P2.Apps.Example", "k3x9p2", false)]
    [InlineData("glucose-today-k3x9p2.apps.example:8443", "k3x9p2", false)]
    [InlineData("glucose-today-k3x9p2.apps.example.", "k3x9p2", false)]
    // A slug that itself ends in -preview, or carries a six-character part, is still read by the id.
    [InlineData("my-preview-k3x9p2.apps.example", "k3x9p2", false)]
    [InlineData("my-preview-k3x9p2-preview.apps.example", "k3x9p2", true)]
    [InlineData("abcdef-k3x9p2.apps.example", "k3x9p2", false)]
    [InlineData("a-b-c-k3x9p2-preview.apps.example", "k3x9p2", true)]
    public void ALabelUnderTheAppsDomain_IsReadByItsId(string host, string id, bool preview)
    {
        var origin = AppOrigin.Parse(host, Domain);

        origin.ShouldNotBeNull();
        origin.Id.ShouldBe(id);
        origin.Preview.ShouldBe(preview);
    }

    // A renamed app's old link names its old slug: the id alone finds it.
    [Fact]
    public void TwoSlugsBeforeOneId_NameTheSameApp() =>
        AppOrigin.Parse("old-name-k3x9p2.apps.example", Domain)!.Id
            .ShouldBe(AppOrigin.Parse("new-name-k3x9p2.apps.example", Domain)!.Id);

    [Theory]
    [InlineData("k3x9p2.apps.example")]
    [InlineData("-k3x9p2.apps.example")]
    [InlineData("glucose-today.apps.example")]
    [InlineData("glucose-today-k3x9p2x.apps.example")]
    [InlineData("glucose-today-k3x9p.apps.example")]
    [InlineData("glucose-today-k3x9p2-draft.apps.example")]
    [InlineData("glucose_today-k3x9p2.apps.example")]
    [InlineData("glucose-today-k3x9p2.preview.apps.example")]
    [InlineData("a.glucose-today-k3x9p2.apps.example")]
    [InlineData("apps.example")]
    [InlineData("glucose-today-k3x9p2.apps.example.evil")]
    [InlineData("glucose-today-k3x9p2.otherapps.example")]
    [InlineData("glucose-today-k3x9p2xapps.example")]
    [InlineData("assistants.herfluffness.com")]
    [InlineData("")]
    public void AnythingElse_IsNoApp(string host) => AppOrigin.Parse(host, Domain).ShouldBeNull();

    [Fact]
    public void ThePreviewsLink_IsTheLabelWithPreviewAppended() =>
        BuiltAppId.PreviewLink("glucose-today", "k3x9p2", Domain)
            .ShouldBe("https://glucose-today-k3x9p2-preview.apps.example/");

    // What the server makes of its own links: the published one and the preview read back to the id.
    [Fact]
    public void BothLinks_ReadBackToTheirApp()
    {
        AppOrigin.Parse(new Uri(BuiltAppId.Link("glucose-today", "k3x9p2", Domain)).Authority, Domain)
            .ShouldBe(new AppOrigin("k3x9p2", Preview: false));
        AppOrigin.Parse(new Uri(BuiltAppId.PreviewLink("glucose-today", "k3x9p2", Domain)).Authority, Domain)
            .ShouldBe(new AppOrigin("k3x9p2", Preview: true));
    }
}