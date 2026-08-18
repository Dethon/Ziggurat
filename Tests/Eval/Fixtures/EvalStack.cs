using System.Net;
using System.Text.Json;
using Agent.Modules;
using Agent.Settings;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Clients.Voice;
using Mcp.Hosting;
using McpServerTimers.Modules;
using McpServerTimers.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Tests.Eval.Harness;
using Tests.Integration.Fixtures;

namespace Tests.Eval.Fixtures;

// Everything one scenario runs against: the real MCP servers in process, the outermost externals
// faked, and the agent built by the real factory from the shipped definition in
// Agent/appsettings.json. Only the configured endpoint urls are rewritten — reconstructing the
// wiring here would test a composition no deployment uses, which is the failure ADR-0030 exists
// to prevent.
public sealed class EvalStack : IAsyncDisposable
{
    // The satellites a scenario's voice turn can come from and ring on. Two rooms, because a
    // one-room roster cannot tell "defaulted to the speaking room" from "picked the only option".
    public static readonly SatelliteDescriptor[] Roster =
    [
        new("kitchen-01", "kitchen"),
        new("office-01", "office")
    ];

    private readonly List<IHost> _servers = [];
    private ServiceProvider? _agentServices;

    public FakeVoiceHub VoiceHub { get; } = new(Roster);

    // One instant for the agent's decoration and for every server, so an expected fire time is an
    // exact string. It never advances: a clock that ticks would make a timer's remaining seconds a
    // range, and a scenario asserting a range asserts almost nothing.
    public FakeTimeProvider Clock { get; private set; } = null!;

    public IAgentFactory Factory { get; private set; } = null!;

    public static async Task<EvalStack> StartAsync(
        Scenario scenario, string redisConnectionString, IToolInvocationObserver observer)
    {
        // Started as far back as the oldest running timer needs, then wound forward to the turn's
        // own instant once everything is armed. A clock that only ever sat at the instant would
        // arm every timer with its full duration left.
        var stack = new EvalStack
        {
            Clock = new FakeTimeProvider(
                scenario.Instant - scenario.Armed.Select(a => a.RunningFor).DefaultIfEmpty().Max())
        };

        // Madrid, because the deployment's satellites are there and a scheduled time is only exact
        // once the zone is. The provider carries it so every server and the turn decoration agree.
        stack.Clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"));

        var timers = await stack.StartTimersAsync(scenario);
        stack.BuildAgentServices(redisConnectionString, observer, new Dictionary<string, string>
        {
            ["mcp-timers"] = timers
        });

        return stack;
    }

    private async Task<string> StartTimersAsync(Scenario scenario)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureTimers(new TimerSettings());

        // After the server's own registration, so the last word is the pinned one. The hub is
        // replaced at the transport rather than at the adapter: the adapters, their token header
        // and their failure mapping are production code a scenario is entitled to exercise.
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => (TimeProvider)Clock));
        builder.Services.AddHttpClient(VoiceHubHttp.ClientName, client =>
            {
                client.BaseAddress = new Uri("http://voice-hub.eval/");
            })
            .ConfigurePrimaryHttpMessageHandler(() => VoiceHub);

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        _servers.Add(app);

        await ArmAsync(app.Services.GetRequiredService<TimerFileSystem>(), scenario);

        return $"http://localhost:{port}/mcp";
    }

    // Through the server's own create path, with the same JSON a model would write. Writing into
    // the store directly would arm a timer the server itself could not have produced, and a
    // scenario about changing a running timer would then be reading a state that never occurs.
    private async Task ArmAsync(TimerFileSystem timers, Scenario scenario)
    {
        foreach (var seed in scenario.Armed.OrderByDescending(a => a.RunningFor))
        {
            Clock.SetUtcNow(scenario.Instant - seed.RunningFor);

            var body = JsonSerializer.Serialize(new
            {
                durationSeconds = seed.DurationSeconds,
                text = seed.Text,
                target = new { room = seed.Room }
            });

            var result = await timers.CreateAsync(
                $"/{seed.Id}/timer.json", body, overwrite: false, createDirectories: true,
                CancellationToken.None);

            if (result is FsResult<FsCreateResult>.Err error)
            {
                throw new InvalidOperationException(
                    $"Could not arm '{seed.Id}' for the scenario: {error}");
            }
        }

        Clock.SetUtcNow(scenario.Instant);
    }

    // The shipped configuration, with two edits and no third: the endpoints point at the servers
    // this stack hosts, and the secrets come from user secrets rather than from the environment
    // the container would have had.
    private void BuildAgentServices(
        string redisConnectionString, IToolInvocationObserver observer,
        IReadOnlyDictionary<string, string> hosted)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ShippedSettingsPath(), optional: false)
            .AddUserSecrets<EvalStack>()
            .AddEnvironmentVariables()
            .Build();

        var shipped = configuration.Get<AgentSettings>()
                      ?? throw new InvalidOperationException("Agent/appsettings.json did not bind.");

        var settings = shipped with
        {
            Redis = new RedisConfiguration { ConnectionString = redisConnectionString },
            Agents = [.. shipped.Agents.Select(a => a with { McpServerEndpoints = Rewrite(a, hosted) })],
            // Nothing dials a channel in an eval: the boundary is the agent, and what a channel
            // does with a reply afterwards is covered by the channel and end-to-end suites.
            ChannelEndpoints = []
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgent(settings);
        services.AddSubAgents(settings.SubAgents);
        services.AddSingleton(observer);

        _agentServices = services.BuildServiceProvider();
        Factory = _agentServices.GetRequiredService<IAgentFactory>();
    }

    // An endpoint with nothing hosting it is dropped rather than left pointing at a compose host
    // name: a configured endpoint that cannot be dialled fails the whole session (ADR-0027), so
    // the alternative is a scenario that cannot run at all. The agent under test therefore has a
    // smaller toolset than the shipped one until every family's servers are hosted, and which
    // sections it actually got is readable in the dump's assembled prompt.
    private static string[] Rewrite(AgentDefinition agent, IReadOnlyDictionary<string, string> hosted) =>
        [.. agent.McpServerEndpoints
            .Select(endpoint => hosted.FirstOrDefault(h => endpoint.Contains(h.Key)).Value)
            .Where(rewritten => rewritten is not null)];

    private static string ShippedSettingsPath() =>
        Path.Combine(RepositoryRoot.Path, "Agent", "appsettings.json");

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_servers.Select(async server =>
        {
            await server.StopAsync();
            server.Dispose();
        }));

        if (_agentServices is not null)
        {
            await _agentServices.DisposeAsync();
        }
    }
}