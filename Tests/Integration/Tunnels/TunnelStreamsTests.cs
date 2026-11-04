using Domain.Security.Signing;
using Infrastructure.Tunnels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// The agent's own requests to a listener of one household's tunnelled server: a transport whose every
// connection is a stream of that household's tunnel of that name, under a tag. The relay and the
// servers are the real things on real sockets; each server answers who it is on its tagged listener.
public class TunnelStreamsTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string ServerName = "lemonade";
    private const string Tag = "chat";

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private string _casa = null!;
    private string _piso = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _casa = (await _accounts.MintOperatorAsync("Fran")).TenantId;
        _piso = await _accounts.SeedHouseholdAsync("Piso");
        _relay = await RelayHost.StartAsync(
            _accounts, AgentCredential, redis.ConnectionString,
            SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory }), _metrics);
    }

    public async Task DisposeAsync()
    {
        _released.TrySetResult();
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public async Task ARequestThroughAHouseholdsStreams_ReachesItsTaggedListener_AndItsAnswerStreamsBack()
    {
        await using var server = await StartAsync(_casa, "casa");
        using var client = Client(_casa);

        (await client.GetStringAsync("/who")).ShouldBe("casa");

        using var response = await client.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        (await reader.ReadLineAsync()).ShouldBe("first", "the first chunk arrives while the server still holds the rest");
        _released.SetResult();
        (await reader.ReadToEndAsync()).ShouldBe("last");
    }

    [Fact]
    public async Task AnotherHouseholdsServerOfTheSameName_IsNeverReached()
    {
        await using var piso = await StartAsync(_piso, "piso");
        await using var casa = await StartAsync(_casa, "casa");
        using var toCasa = Client(_casa);
        using var toPiso = Client(_piso);

        (await toCasa.GetStringAsync("/who")).ShouldBe("casa");
        (await toPiso.GetStringAsync("/who")).ShouldBe("piso");
    }

    [Fact]
    public async Task AHouseholdWithNoSuchServer_FailsAsOffline()
    {
        await using var piso = await StartAsync(_piso, "piso");
        using var client = Client(_casa);

        var failure = await Should.ThrowAsync<HttpRequestException>(() => client.GetStringAsync("/who"));

        failure.ToString().ShouldContain($"{ServerName} is offline");
    }

    [Fact]
    public async Task AClosedTunnel_FailsAsOffline_AndOnceReopenedIsReachedThroughTheSameTransport()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_casa, ServerName);
        using var client = Client(_casa);
        var first = await StartOnAsync(credential.Credential, "before");
        (await client.GetStringAsync("/who")).ShouldBe("before");

        await first.DisposeAsync();
        await Eventually.Until(() => _relay.Registry.Find(_casa, ServerName) is null, "the tunnel closes");
        var failure = await Should.ThrowAsync<HttpRequestException>(() => client.GetStringAsync("/who"));
        failure.ToString().ShouldContain($"{ServerName} is offline");

        await using var reopened = await StartOnAsync(credential.Credential, "after");
        (await client.GetStringAsync("/who")).ShouldBe("after");
    }

    [Fact]
    public async Task ATagTheServerDoesNotTake_FailsTheRequest()
    {
        await using var server = await StartAsync(_casa, "casa");
        using var client = new HttpClient(_relay.Registry.StreamsTo(_casa, ServerName, "pages"))
        {
            BaseAddress = new Uri("http://lemonade.tunnel")
        };

        await Should.ThrowAsync<HttpRequestException>(() => client.GetStringAsync("/who"));
    }

    private HttpClient Client(string household) =>
        new(_relay.Registry.StreamsTo(household, ServerName, Tag)) { BaseAddress = new Uri("http://lemonade.tunnel") };

    private async Task<TunnelledServer> StartAsync(string household, string answers) =>
        await StartOnAsync((await _accounts.MintTunnelCredentialAsync(household, ServerName)).Credential, answers, household);

    private async Task<TunnelledServer> StartOnAsync(string credential, string answers, string? household = null)
    {
        var server = await TunnelledServer.StartAsync(
            _relay.Hub, credential, ServerName,
            listeners: new Dictionary<string, int> { [Tag] = TestPort.GetAvailable() },
            routes: app =>
            {
                app.MapGet("/who", () => answers);
                app.MapGet("/stream", async (HttpContext http) =>
                {
                    await http.Response.WriteAsync("first\n");
                    await http.Response.Body.FlushAsync();
                    await _released.Task.WaitAsync(http.RequestAborted);
                    await http.Response.WriteAsync("last");
                });
            });
        await Eventually.Until(
            () => _relay.Registry.Find(household ?? _casa, ServerName) is { } open && open.CredentialId != "",
            $"{answers}'s tunnel opens");
        return server;
    }
}