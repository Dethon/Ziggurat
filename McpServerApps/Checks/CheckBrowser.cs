using System.Text.Json;
using System.Text.Json.Serialization;
using McpServerApps.Settings;
using Microsoft.Playwright;

namespace McpServerApps.Checks;

// The one browser a check runs, wherever it runs. The options are built once and either launch it here
// or travel to a Playwright browser server (Checks:Browser:Endpoint) that launches it with them, so the
// two never differ in anything but the machine.
//
// - **Full Chromium in its new headless mode**: the headless shell ignores the secure-origin flag.
// - **Chromium's own sandbox, always**: the agent's code runs in the page. Playwright's default is
//   `--no-sandbox`, and a browser server drops a sandbox it is not asked for.
// - **The apps domain resolves to this server's pages listener** (Checks:Browser:Pages, else this
//   machine's loopback), never through public DNS; the page arrives by Host as any app origin's does.
// - **Its way out is the egress proxy** (Checks:Browser:Proxy, CheckEgress) when there is one: in the
//   deployment its network has no other.
// - **The preview is a secure context**, as it is behind the proxy on https: its `__Host-` session cookie
//   and its worker need one, and plain http under a real domain is none. The flag is the process's, so
//   each check has a browser of its own, and closing it is how the deadline stops everything at once.
internal static class CheckBrowser
{
    // Where a browser server reads the options to launch a connection's browser with.
    private const string LaunchOptionsHeader = "x-playwright-launch-options";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static BrowserTypeLaunchOptions Options(AppsSettings settings, string address)
    {
        var browser = settings.Checks.Browser;
        var pages = browser.Pages is "" ? $"127.0.0.1:{settings.Listeners.Pages}" : browser.Pages;
        // Everything but the app origins through the egress proxy, loopback included (Chromium sends
        // loopback direct unless told), when there is one. The later rule wins, so the apps domain comes
        // after `<-loopback>`: a local apps name under .localhost is loopback too, and still goes direct.
        string[] egress = browser.Proxy is ""
            ? []
            : [$"--proxy-server=http://{browser.Proxy}", $"--proxy-bypass-list=<-loopback>;*.{settings.AppsDomain}"];
        return new BrowserTypeLaunchOptions
        {
            Channel = "chromium",
            Headless = true,
            ChromiumSandbox = true,
            Timeout = (float)settings.Checks.Deadline.TotalMilliseconds,
            Args =
            [
                $"--host-resolver-rules=MAP *.{settings.AppsDomain} {pages}",
                $"--unsafely-treat-insecure-origin-as-secure={address}",
                .. egress
            ]
        };
    }

    public static Task<IBrowser> OpenAsync(IPlaywright playwright, CheckBrowserSettings browser, BrowserTypeLaunchOptions options) =>
        browser.Endpoint is ""
            ? playwright.Chromium.LaunchAsync(options)
            : playwright.Chromium.ConnectAsync(browser.Endpoint, new BrowserTypeConnectOptions
            {
                Timeout = options.Timeout,
                Headers = new Dictionary<string, string> { [LaunchOptionsHeader] = JsonSerializer.Serialize(options, _json) }
            });
}