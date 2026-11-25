using Domain.Apps;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// The app sign-in returns a person only to a built app's origin under the apps domain, and the
// callback there sends them on only to a path of its own origin, so no link can carry a code, or a
// signed-in person, anywhere else.
public class AppSignInTests
{
    private const string Domain = "apps.example";

    [Theory]
    [InlineData("https://glucose-k3x9p2.apps.example/", "https://glucose-k3x9p2.apps.example", "/")]
    [InlineData("https://glucose-k3x9p2.apps.example", "https://glucose-k3x9p2.apps.example", "/")]
    [InlineData("https://Glucose-K3X9P2-preview.Apps.Example/week?d=1", "https://glucose-k3x9p2-preview.apps.example", "/week?d=1")]
    [InlineData("https://glucose-k3x9p2.apps.example:8443/x", "https://glucose-k3x9p2.apps.example:8443", "/x")]
    public void AnAppsAddress_ReturnsToItsOriginAndPath(string url, string origin, string path)
    {
        var target = AppSignIn.ReturnTo(url, Domain);

        target.ShouldNotBeNull();
        target.Origin.ShouldBe(origin);
        target.Path.ShouldBe(path);
    }

    [Theory]
    // Not under the apps domain, or not one app's label under it.
    [InlineData("https://assistants.herfluffness.com/")]
    [InlineData("https://apps.example/")]
    [InlineData("https://glucose-k3x9p2.apps.example.evil.com/")]
    [InlineData("https://glucose-k3x9p2.evilapps.example/")]
    [InlineData("https://glucose-k3x9p2.apps.example@evil.com/")]
    [InlineData("https://evil.com/?https://glucose-k3x9p2.apps.example/")]
    [InlineData("https://glucose.apps.example/")]
    [InlineData("https://a.glucose-k3x9p2.apps.example/")]
    // Somebody's name and password in the address.
    [InlineData("https://user:pass@glucose-k3x9p2.apps.example/")]
    // Plain http, outside a loopback name.
    [InlineData("http://glucose-k3x9p2.apps.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/glucose")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_IsNoReturn(string? url) => AppSignIn.ReturnTo(url, Domain).ShouldBeNull();

    // A deployment with no apps domain yet returns to nothing at all.
    [Fact]
    public void NoAppsDomain_ReturnsToNothing() =>
        AppSignIn.ReturnTo("https://glucose-k3x9p2.apps.example/", "").ShouldBeNull();

    // A stack on somebody's own machine serves apps on plain http under .localhost, which is loopback by name.
    [Fact]
    public void PlainHttp_IsAReturnUnderALoopbackName()
    {
        var target = AppSignIn.ReturnTo("http://glucose-k3x9p2.apps.localhost:6019/", "apps.localhost");

        target.ShouldNotBeNull().Origin.ShouldBe("http://glucose-k3x9p2.apps.localhost:6019");
    }

    [Fact]
    public void TheCallback_IsOnTheOrigin_CarryingTheCodeAndThePath()
    {
        var callback = new Uri(AppSignIn.Callback("https://glucose-k3x9p2.apps.example", "the-code", "/week?d=1&x=2"));

        callback.GetLeftPart(UriPartial.Path).ShouldBe("https://glucose-k3x9p2.apps.example" + AppSignIn.CallbackPath);
        var query = System.Web.HttpUtility.ParseQueryString(callback.Query);
        query[AppSignIn.CodeParameter].ShouldBe("the-code");
        query[AppSignIn.PathParameter].ShouldBe("/week?d=1&x=2");
    }

    [Theory]
    [InlineData("/week?d=1", "/week?d=1")]
    [InlineData("/", "/")]
    [InlineData("//evil.com/", "/")]
    [InlineData("/\\evil.com/", "/")]
    [InlineData("https://evil.com/", "/")]
    [InlineData("week", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void TheCallback_GoesOnOnlyToAPathOfItsOwnOrigin(string? path, string expected) =>
        AppSignIn.SamePath(path).ShouldBe(expected);
}