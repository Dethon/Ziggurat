using Accounts.Pages;
using Shouldly;

namespace Tests.Unit.Accounts;

// A signed-in device is listed by what a person calls it — the browser and the system it runs on —
// rather than the user agent it sent, which is three lines of compatibility tokens nobody can read.
public sealed class DeviceNamesTests
{
    [Theory]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36",
        "Chrome on Android")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1",
        "Safari on iPhone")]
    [InlineData(
        "Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        "Safari on iPad")]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15",
        "Safari on Mac")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
        "Edge on Windows")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Firefox on Windows")]
    [InlineData(
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/153.0.8010.12 Safari/537.36",
        "Chrome on Linux")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36",
        "Samsung Internet on Android")]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/131.0.6778.73 Mobile/15E148 Safari/604.1",
        "Chrome on iPhone")]
    public void Of_ABrowsersUserAgent_IsTheBrowserOnItsSystem(string userAgent, string expected) =>
        DeviceNames.Of(userAgent).ShouldBe(expected);

    // What it cannot place it shows as it was sent, so a device is never listed as something it is not.
    [Theory]
    [InlineData("Pixel phone browser", "Pixel phone browser")]
    [InlineData("curl/8.5.0", "curl/8.5.0")]
    [InlineData("", "Unknown device")]
    [InlineData(null, "Unknown device")]
    [InlineData("   ", "Unknown device")]
    public void Of_AnythingElse_IsWhatWasSent(string? userAgent, string expected) =>
        DeviceNames.Of(userAgent).ShouldBe(expected);
}