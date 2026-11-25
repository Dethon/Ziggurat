using McpServerApps.Checks;
using McpServerApps.Settings;
using Shouldly;

namespace Tests.Unit.McpServerApps;

// The browser reaches the app at the apps server's pages listener: on this machine its loopback, in the
// deployment the address it is told (the browser runs in a container of its own). Both resolve to loopback
// in the suite's browser cases, so only this tells them apart.
public class CheckBrowserTests
{
    private const string Preview = "http://glucose-abc123-preview.apps.test";

    [Fact]
    public void ABrowserToldWhereThePagesAre_MapsTheAppsDomainThere()
    {
        var settings = new AppsSettings
        {
            AppsDomain = "apps.test",
            Checks = new AppCheckSettings { Browser = new CheckBrowserSettings { Pages = "mcp-apps:8081" } }
        };

        CheckBrowser.Options(settings, Preview).Args!
            .ShouldContain("--host-resolver-rules=MAP *.apps.test mcp-apps:8081");
    }

    [Fact]
    public void ABrowserToldNothing_MapsTheAppsDomainToThisMachinesPagesListener()
    {
        var settings = new AppsSettings { AppsDomain = "apps.test", Listeners = new AppListeners { Pages = 6019 } };

        var options = CheckBrowser.Options(settings, Preview);

        options.Args!.ShouldBe([
            "--host-resolver-rules=MAP *.apps.test 127.0.0.1:6019",
            $"--unsafely-treat-insecure-origin-as-secure={Preview}"
        ]);
        options.ChromiumSandbox.ShouldBe(true);
    }
}