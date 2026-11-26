using McpServerApps.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.McpServerApps;

// What the image starts with: every test hands the server its settings in C#, so only this reads the
// shipped file. An empty CDN list would refuse every pinned library a built app loads.
public class AppsShippedSettingsTests
{
    [Fact]
    public void TheShippedSettings_NameTheCdnsAndBothListeners()
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TestHelpers.FindSolutionRoot(), "McpServerApps", "appsettings.json"), optional: false)
            .Build()
            .Get<AppsSettings>()!;

        settings.Cdns.ShouldBe(["https://cdn.jsdelivr.net", "https://unpkg.com", "https://esm.sh"]);
        settings.Listeners.ShouldBe(new AppListeners { Mcp = 8080, Pages = 8081 });
    }

    // What the file says is what the code defaults to, so a host run and the image bound asks alike.
    [Fact]
    public void TheShippedSettings_BoundAsksAsTheCodeDoes()
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TestHelpers.FindSolutionRoot(), "McpServerApps", "appsettings.json"), optional: false)
            .Build()
            .Get<AppsSettings>()!;

        settings.Asks.ShouldBe(new AppAskSettings());
    }
}