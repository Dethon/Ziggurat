using McpServerWebSearch.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpServerWebSearch;

// The tab cap and the session idle timeout are ordinary settings on the browse server — generic
// tunables in its own appsettings.json, no compose entry — with the defaults the spec names.
public class BrowsingSettingsTests
{
    [Fact]
    public void AnAbsentSection_BindsTheDefaults_ThreeTabsAndThirtyMinutes()
    {
        var settings = new ConfigurationBuilder().Build()
            .GetSection("Browsing").Get<BrowsingConfiguration>() ?? new BrowsingConfiguration();

        settings.TabCap.ShouldBe(3);
        settings.SessionIdleTimeoutMinutes.ShouldBe(30);
    }

    [Fact]
    public void AConfiguredSection_OverridesBothTunables()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Browsing:TabCap"] = "5",
                ["Browsing:SessionIdleTimeoutMinutes"] = "10"
            })
            .Build();

        var settings = configuration.GetSection("Browsing").Get<BrowsingConfiguration>()!;

        settings.TabCap.ShouldBe(5);
        settings.SessionIdleTimeoutMinutes.ShouldBe(10);
    }

    [Fact]
    public void TheShippedAppSettings_CarryTheSection()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ziggurat.sln")))
        {
            dir = dir.Parent;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(dir!.FullName, "McpServerWebSearch/appsettings.json"))
            .Build();

        var settings = configuration.Get<McpSettings>()!;

        settings.Browsing.TabCap.ShouldBe(3);
        settings.Browsing.SessionIdleTimeoutMinutes.ShouldBe(30);
    }
}