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

public class DismissEndpointAuthTests
{
    private static async Task<(HttpClient Client, ActiveAlertRegistry Alerts)> BuildClientAsync(AnnounceSettings announce)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var alerts = new ActiveAlertRegistry();
        builder.Services.AddSingleton(announce).AddSingleton(alerts);
        var app = builder.Build();
        DismissEndpoint.Map(app);
        await app.StartAsync();
        return (app.GetTestClient(), alerts);
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var (client, _) = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        using (client)
        {
            var response = await client.PostAsync("/api/voice/dismiss", null);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Dismiss_WithRingingAlerts_ReturnsThem()
    {
        var (client, alerts) = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        using (client)
        {
            client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");
            alerts.Register(new AlertHandle(new CancellationTokenSource(), ["kitchen-01"], "pasta", AnnounceKind.Timer));

            var response = await client.PostAsync("/api/voice/dismiss", null);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var dismissed = await response.Content.ReadFromJsonAsync<List<DismissedAlert>>();
            dismissed!.ShouldContain(d => d.Text == "pasta" && d.Kind == AnnounceKind.Timer);
        }
    }
}