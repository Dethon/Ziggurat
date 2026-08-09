using System.Net;
using System.Net.Http.Json;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatellitesEndpointTests
{
    private static readonly Dictionary<string, SatelliteConfig> _sample = new()
    {
        ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", Locality = "Madrid, Spain" }
    };

    private static async Task<HttpClient> BuildClientAsync(AnnounceSettings announce)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(announce).AddSingleton(new SatelliteRegistry(_sample));
        var app = builder.Build();
        SatellitesEndpoint.Map(app);
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task Roster_NoToken_Returns401()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        (await client.GetAsync("/api/voice/satellites")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resolve_WrongToken_Returns401()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "wrong");
        var response = await client.PostAsJsonAsync("/api/voice/satellites/resolve", new AnnounceTarget { Room = "Kitchen" });
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Roster_ReturnsIdAndRoom()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");

        var roster = await client.GetFromJsonAsync<List<SatelliteDescriptor>>("/api/voice/satellites");

        roster!.ShouldContain(s => s.Id == "kitchen-01" && s.Room == "Kitchen");
    }

    [Fact]
    public async Task Resolve_DisplayLocationFormRoom_ResolvesToId()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");

        // The agent is shown DisplayLocation, so a room target in that form must resolve on the hub —
        // resolution stays hub-authoritative precisely so the timers server can never diverge here.
        var response = await client.PostAsJsonAsync(
            "/api/voice/satellites/resolve", new AnnounceTarget { Room = "Kitchen (Madrid, Spain)" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ids = await response.Content.ReadFromJsonAsync<List<string>>();
        ids!.ShouldBe(["kitchen-01"]);
    }
}