using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.Tools.FileSystem.Bridge;
using global::Agent.App;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.Unit.Agent;

// The bridge over HTTP: one request per kernel operation, the call token as the only credential.
// What each operation means is VfsCall's and is asserted at the exec tool's seam; this pins the
// wire — the token gate, the answer shapes the daemon parses, and that the public proxy never
// reaches it.
public class VfsBridgeApiTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly VfsBridge _bridge;

    public VfsBridgeApiTests()
    {
        _bridge = new VfsBridge(_time);
    }

    private VfsCall Mint(Func<string, bool>? permits = null) => _bridge.Mint(
        BridgeFixtures.Registry((new MemoryDisk("vault", new Dictionary<string, string>
        {
            ["notes/a.md"] = "alpha\n"
        }), "/vault", null)),
        permits ?? (_ => true));

    public static TheoryData<string> Operations => ["attr", "list", "read", "write", "delete"];

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task AnOperationWithNoTokenOrAnUnknownOne_IsUnauthorized(string op)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var none = await client.PostAsync($"/api/vfs-bridge/{op}?path=/vault", null);
        using var unknown = await client.SendAsync(Request(op, "/vault", "not-a-token"));

        none.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACompletedToken_IsUnauthorized()
    {
        var call = Mint();
        _bridge.Complete(call.Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("read", "/vault/notes/a.md", call.Token));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // A token whose exec never returned is reclaimed once it outlives any timeout a sandbox accepts.
    [Fact]
    public async Task AnExpiredToken_IsUnauthorized()
    {
        var call = Mint();
        _time.Advance(VfsBridge.Lifetime + TimeSpan.FromSeconds(1));
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("read", "/vault/notes/a.md", call.Token));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Attr_AnswersKindAndSize()
    {
        var call = Mint();
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("attr", "/vault/notes/a.md", call.Token));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["kind"]!.GetValue<string>().ShouldBe("file");
        body["size"]!.GetValue<long>().ShouldBe(6);
    }

    [Fact]
    public async Task List_AnswersEntriesWithTheirKinds()
    {
        var call = Mint();
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("list", "/vault", call.Token));

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["entries"]!.AsArray().Select(e => (e!["name"]!.GetValue<string>(), e["kind"]!.GetValue<string>()))
            .ShouldBe([("notes", "dir")]);
        body["truncated"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Read_AnswersTheBytes()
    {
        var call = Mint();
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("read", "/vault/notes/a.md", call.Token));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()).ShouldBe("alpha\n");
    }

    [Fact]
    public async Task Write_TakesTheWholeFileAsTheBody()
    {
        var call = Mint();
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/vfs-bridge/write?path=%2Fvault%2Fnotes%2Fb.md&new=true")
        {
            Content = new ByteArrayContent("beta\n"u8.ToArray())
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", call.Token);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var change = call.Changes.ShouldHaveSingleItem();
        (change.Path, change.Operation, change.Status).ShouldBe(("/vault/notes/b.md", "create", "applied"));
    }

    [Fact]
    public async Task Revoke_MarksTheCallRevoked()
    {
        var call = Mint();
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/vfs-bridge/revoke");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", call.Token);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        call.Revoked.ShouldBeTrue();
    }

    // A refusal carries the errno the daemon hands the kernel and the mount's own envelope.
    [Fact]
    public async Task ARefusal_AnswersItsErrnoAndTheEnvelope()
    {
        var call = Mint(_ => false);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("read", "/vault/notes/a.md", call.Token));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["errno"]!.GetValue<string>().ShouldBe("EACCES");
        body["error"]!["errorCode"]!.GetValue<string>().ShouldBe("permission_denied");
    }

    // The bridge answers sandbox commands on the deployment's own network; the public proxy routes
    // /api/agents* to the agent and must never route this.
    [Fact]
    public void ThePublicProxy_DoesNotRouteTheBridge()
    {
        var caddyfile = File.ReadAllText(Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose", "caddy", "Caddyfile"));

        var agentRoutes = Regex.Matches(caddyfile, @"handle\s+(?<matcher>\S+)\s*\{\s*reverse_proxy\s+agent:")
            .Select(m => m.Groups["matcher"].Value)
            .ToList();

        agentRoutes.ShouldNotBeEmpty("the proxy routes the registration API to the agent, so this proves something");
        agentRoutes.ShouldAllBe(matcher => !VfsBridgeApi.Route.StartsWith(matcher.TrimEnd('*'), StringComparison.Ordinal));
        Regex.IsMatch(caddyfile, @"handle\s*\{\s*reverse_proxy\s+agent:").ShouldBeFalse("a catch-all to the agent would route it");
    }

    private static HttpRequestMessage Request(string op, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/vfs-bridge/{op}?path={Uri.EscapeDataString(path)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_bridge);

        var app = builder.Build();
        app.MapVfsBridge();
        await app.StartAsync();
        return app;
    }
}