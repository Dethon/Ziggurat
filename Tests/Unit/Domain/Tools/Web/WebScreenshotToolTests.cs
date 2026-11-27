using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Web;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class WebScreenshotToolTests
{
    private static readonly byte[] _jpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    private readonly Mock<IWebBrowser> _browser = new();

    [Fact]
    public async Task AScreenshot_ArrivesAsOnePictureNamingItsPageAndScreen()
    {
        Answers(new ScreenshotResult("s1", ScreenshotStatus.Success, "https://example.com/menu", "Lunch menu",
            "image/jpeg", _jpeg)
        { Screen = 2, Screens = 5 });

        var result = await RunAsync(screen: 2);

        var envelope = result.Envelope.AsObject();
        envelope["status"]!.GetValue<string>().ShouldBe("success");
        envelope["url"]!.GetValue<string>().ShouldBe("https://example.com/menu");
        envelope["title"]!.GetValue<string>().ShouldBe("Lunch menu");
        envelope["screen"]!.GetValue<int>().ShouldBe(2);
        envelope["screens"]!.GetValue<int>().ShouldBe(5);

        var image = result.Image.ShouldNotBeNull();
        image.Bytes.ShouldBe(_jpeg);
        image.MediaType.ShouldBe("image/jpeg");
        // The picture's own envelope is the one hydration reads back: a screenshot, named by its
        // page and screen so a note left in its place says which.
        var read = PageImageResult.TryRead(image.Envelope).ShouldNotBeNull();
        read.Shown.ShouldBeTrue();
        read.Screenshot.ShouldBe(true);
        read.Label.ShouldContain("Lunch menu");
        read.Label.ShouldContain("2 of 5");
        read.SizeBytes.ShouldBe(_jpeg.Length);
    }

    [Fact]
    public async Task APageWithNoTitle_IsNamedByItsAddress()
    {
        Answers(new ScreenshotResult("s1", ScreenshotStatus.Success, "https://example.com/menu", " ",
            "image/jpeg", _jpeg)
        { Screen = 1, Screens = 1 });

        var result = await RunAsync();

        PageImageResult.TryRead(result.Image!.Envelope)!.Label.ShouldContain("https://example.com/menu");
    }

    [Fact]
    public async Task NoScreenAskedFor_ShowsThePageWhereItStands()
    {
        Answers(new ScreenshotResult("s1", ScreenshotStatus.Success, "https://example.com", "x",
            "image/jpeg", _jpeg)
        { Screen = 3, Screens = 4 });

        await RunAsync();

        _browser.Verify(b => b.ScreenshotAsync(
            It.Is<ScreenshotRequest>(r => r.SessionId == "s1" && r.Screen == null),
            It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task AScreenBelowOne_IsRefusedWithoutReachingTheBrowser(int screen)
    {
        var result = await RunAsync(screen);

        result.Image.ShouldBeNull();
        result.Envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        _browser.Verify(b => b.ScreenshotAsync(It.IsAny<ScreenshotRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NoSession_SaysToBrowseFirst()
    {
        Answers(new ScreenshotResult("s1", ScreenshotStatus.SessionNotFound));

        var result = await RunAsync();

        result.Image.ShouldBeNull();
        result.Envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.SessionNotFound);
        result.Envelope.ToJsonString().ShouldContain("web_browse");
    }

    [Fact]
    public async Task AClosedTab_NamesThePageToBrowseAgain()
    {
        Answers(new ScreenshotResult("s1", ScreenshotStatus.TabClosed, Url: "https://example.com/gone"));

        var result = await RunAsync();

        result.Image.ShouldBeNull();
        result.Envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
        result.Envelope.ToJsonString().ShouldContain("https://example.com/gone");
    }

    [Fact]
    public async Task ABrowserError_CarriesItsMessage()
    {
        Answers(new ScreenshotResult("s1", ScreenshotStatus.Error, ErrorMessage: "Timeout 10000ms exceeded."));

        var result = await RunAsync();

        result.Image.ShouldBeNull();
        result.Envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InternalError);
        result.Envelope.ToJsonString().ShouldContain("Timeout 10000ms exceeded.");
    }

    private void Answers(ScreenshotResult result) =>
        _browser.Setup(b => b.ScreenshotAsync(It.IsAny<ScreenshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private Task<WebScreenshotToolResult> RunAsync(int? screen = null) =>
        new Testable(_browser.Object).Run("s1", screen);

    private sealed class Testable(IWebBrowser browser) : WebScreenshotTool(browser)
    {
        public Task<WebScreenshotToolResult> Run(string sessionId, int? screen) =>
            RunAsync(sessionId, screen, CancellationToken.None);
    }
}