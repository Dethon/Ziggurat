using Domain.Contracts;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Clients.Voice;
using Infrastructure.Timers;
using McpServerTimers.Modules;
using McpServerTimers.Settings;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpServerTimers;

public class ConfigModuleTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureTimers(new TimerSettings
        {
            VoiceHub = new() { BaseUrl = "http://mcp-channel-voice:8080" },
            Announce = new() { Token = "t" }
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfigureTimers_ResolvesTimerFileSystem()
    {
        using var sp = Build();
        sp.GetRequiredService<TimerFileSystem>().ShouldNotBeNull();
    }

    [Fact]
    public void ConfigureTimers_RegistersTheNamedVoiceHubClient()
    {
        using var sp = Build();

        // The adapters create this named client on every hub call; if its registration drifts
        // from VoiceHubHttp.ClientName they get an unconfigured client (no BaseAddress) at runtime.
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(VoiceHubHttp.ClientName);

        client.BaseAddress.ShouldBe(new Uri("http://mcp-channel-voice:8080"));
        client.Timeout.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void ConfigureTimers_BacksHubContractsWithHttpAdapters()
    {
        using var sp = Build();

        // The three hub-local capabilities are HTTP adapters here, not the in-process registries, and
        // each household's are bound to it.
        var voice = sp.GetRequiredService<IHouseholdVoices>().For("0a1b");
        voice.Announcer.ShouldBeOfType<HttpInsistentAnnouncer>();
        voice.Dismisser.ShouldBeOfType<HttpAlertDismisser>();
        voice.Satellites.ShouldBeOfType<HttpSatelliteCatalog>();
        sp.GetRequiredService<ITimerStore>().ShouldBeOfType<InMemoryTimerStore>();
    }
}