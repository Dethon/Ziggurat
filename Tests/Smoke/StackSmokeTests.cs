using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.WebChat;
using Tests.Unit.Deployment;

namespace Tests.Smoke;

// What the deployment's compose file and Caddyfile promise, checked against a stack running them
// (scripts/smoke-stack.sh up, then test): who gets past the proxy, what the sandbox can reach, what is
// published, that what the issuers sign is what every server and app believes and nothing else is,
// and one signed-in conversation with the real agent. The conformance tests read the files; these ask
// the running thing. Off unless ZIGGURAT_SMOKE=1, since they need that stack.
[Trait("Category", "Smoke")]
public sealed class StackSmokeTests(SmokeStack stack) : IClassFixture<SmokeStack>
{
    private const string SomebodyElse = "91eb477ecd34251c52fd4493523a0334";
    private const string ProtocolVersion = "2026-07-28";

    [SkippableFact]
    public async Task ASignedOutPage_IsSentToSignInAndBack()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var response = await SendAsync(HttpMethod.Get, stack.MainHost, "/", accept: "text/html");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldStartWith($"https://{stack.MainHost}/accounts/sign-in?returnUrl=");
    }

    [SkippableFact]
    public async Task AnIdentityHeaderTheBrowserSends_IsNotBelieved()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var request = stack.Request(HttpMethod.Get, stack.MainHost, "/");
        request.Headers.Add("Accept", "text/html");
        request.Headers.Add("X-Ziggurat-User", SomebodyElse);
        request.Headers.Add("X-Ziggurat-Roles", "owner,operator");

        (await stack.Http.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [SkippableFact]
    public async Task TheChatClient_AndItsHub_AdmitASignedInPerson()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        (await SendAsync(HttpMethod.Get, stack.MainHost, "/", stack.MemberSession, "text/html")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, stack.MainHost, "/hubs/chat/negotiate?negotiateVersion=1")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, stack.MainHost, "/hubs/chat/negotiate?negotiateVersion=1", stack.MemberSession))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task TheAccountsInternalApi_IsNotReachableThroughTheProxy()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        // Every host that routes the accounts service answers its internal API 404 itself; the
        // dashboard routes none, and its session check refuses a signed-out request before anything.
        var answers = await Task.WhenAll(stack.Hosts.Select(async host =>
            (host, (await SendAsync(HttpMethod.Post, host, "/accounts/internal/resolve")).StatusCode)));

        answers.ShouldAllBe(a => a.host == stack.DashboardHost
            ? a.StatusCode == HttpStatusCode.Unauthorized
            : a.StatusCode == HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task TheDashboard_AdmitsTheOperatorAlone()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        (await SendAsync(HttpMethod.Get, stack.DashboardHost, "/", stack.MemberSession, "text/html")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, stack.DashboardHost, "/", stack.OperatorSession, "text/html")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Sugary_AnswersOnlyASignedInPerson_AndNeverItsMcpOutside()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        (await SendAsync(HttpMethod.Get, stack.SugaryHost, "/api/meals")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, stack.SugaryHost, "/api/meals", stack.MemberSession)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, stack.SugaryHost, "/mcp")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The sandbox runs what the model writes: it reaches the agent's bridge and the internet, and
    // nothing that believes the proxy's identity headers.
    [SkippableFact]
    public async Task TheSandbox_ReachesTheAgent_AndNothingThatTrustsTheIdentityHeaders()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        (await SmokeStack.TryDockerAsync("exec", "mcp-sandbox", "getent", "hosts", "agent")).ExitCode.ShouldBe(0);
        string[] trusting = ["mcp-sugary", "mcp-channel-signalr", "accounts", "webui", "observability", "redis"];
        var reached = await Task.WhenAll(trusting.Select(async name =>
            (name, (await SmokeStack.TryDockerAsync("exec", "mcp-sandbox", "getent", "hosts", name)).ExitCode)));

        reached.Where(r => r.ExitCode == 0).Select(r => r.name).ShouldBeEmpty();
    }

    [SkippableFact]
    public async Task OnlyTheProxy_IsPublishedPastLoopback()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var ports = await SmokeStack.DockerAsync("ps", "--filter", "label=com.docker.compose.project=jackbot-smoke",
            "--format", "{{.Names}} {{.Ports}}");

        ports.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("0.0.0.0:", StringComparison.Ordinal) && !line.StartsWith("caddy ", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [SkippableFact]
    public async Task Redis_RefusesAWrongPassword()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var answer = await SmokeStack.DockerAsync("exec", "-e", "REDISCLI_AUTH=", "redis", "redis-cli", "-a", "not-it",
            "--no-auth-warning", "ping");

        // redis-cli reports the refused AUTH on stderr; what the ping itself gets back is NOAUTH.
        answer.ShouldContain("NOAUTH");
        answer.ShouldNotContain("PONG");
    }

    // One conversation the whole way: a signed-in browser through Caddy to the chat client, its hub,
    // the agent and a real model, and the reply back.
    [SkippableFact]
    public async Task ASignedInPerson_GetsAReplyThroughTheProxy()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false",
            Args = [$"--host-resolver-rules={string.Join(',', stack.Hosts.Select(h => $"MAP {h} 127.0.0.1"))}"]
        });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        await context.AddCookiesAsync(
        [
            new Microsoft.Playwright.Cookie
            {
                Name = "ziggurat_session", Value = stack.MemberSession, Domain = stack.MainHost, Path = "/",
                Secure = true, HttpOnly = true
            }
        ]);
        var page = await context.NewPageAsync();
        var tag = Guid.NewGuid().ToString("N")[..4];

        await WebChatE2ETests.GotoWebChatAsync(page, $"https://{stack.MainHost}/");
        await WebChatE2ETests.SelectAgentAsync(page);
        var input = page.Locator("textarea.chat-input");
        await input.FillAsync($"Smoke {tag}: reply with one short sentence.");
        await input.PressAsync("Enter");

        // Whatever comes back first, then that it is no error: a turn the agent refuses — its
        // assertion did not verify, a server refused its call token — is answered too, as an error
        // message with text in it.
        await Assertions.Expect(page.Locator(".chat-message.assistant .message-content").First)
            .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 180_000 });
        await Assertions.Expect(page.Locator(".chat-message.error")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".chat-message.assistant:not(.error) .message-content").First)
            .Not.ToBeEmptyAsync();
    }

    // Holding a server's credential lets a container talk to it; only the agent's signature says who a
    // call acts for. A call naming somebody in its _meta with no token, a garbage one, or one signed
    // with a key the running agent does not publish is refused by every gated server the stack runs —
    // and the last is refused as an unknown key, not as keys it could not fetch, which is what says
    // each server reached the agent's JWKS (MCP__AGENTKEYSURL, the agent's key volume, its listener).
    [SkippableFact]
    public async Task AForgedCallFromInsideTheNetwork_IsRefusedByEveryServer()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var servers = await GatedServersAsync();
        // Every server the agent dials that this stack starts (all but the sandbox, on its own network),
        // so a reader regression cannot shrink "every" to one.
        servers.ShouldBe(["mcp-library", "mcp-vault", "mcp-websearch", "mcp-idealista", "mcp-homeassistant",
            "mcp-channel-signalr", "mcp-scheduling", "mcp-printer", "mcp-timers", "mcp-sugary"], ignoreOrder: true);
        var foreign = new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Agent));

        var refusals = await Task.WhenAll(servers.Select(async server =>
        {
            var credential = (await SmokeStack.DockerAsync("exec", server, "printenv", "MCP__SHAREDSECRET")).Trim();
            var signed = foreign.Sign(IdentityClaims.Person(SomebodyElse, SomebodyElse, ChannelIds.SignalR, ["owner"])
                .Stamped(TokenIssuers.Agent, server, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2)));
            return (server,
                missing: await ForgedCallAsync(server, credential, token: null),
                garbage: await ForgedCallAsync(server, credential, token: "not.a-token"),
                foreignKey: await ForgedCallAsync(server, credential, signed));
        }));

        refusals.ShouldSatisfyAllConditions(refusals.SelectMany(r => new Action[]
        {
            () => r.missing.ShouldContain("Refused (Missing)", customMessage: r.server),
            () => r.garbage.ShouldContain("Refused (Malformed)", customMessage: r.server),
            () => r.foreignKey.ShouldContain("Refused (UnknownKey)", customMessage: r.server)
        }).ToArray());
    }

    // Caddy overwrites the identity headers a browser sends, but anything on the compose network can
    // skip Caddy: the apps behind it must believe the signed assertion alone. Plain headers naming an
    // operator, with the secret where an app asks for one, are signed out; so is an assertion signed
    // with a key the stack's accounts service does not publish.
    [SkippableFact]
    public async Task TheAppsBehindTheProxy_BelieveNoIdentityTheAccountsServiceDidNotSign()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var plain = new Dictionary<string, string>
        {
            ["X-Ziggurat-User"] = SomebodyElse,
            ["X-Ziggurat-Tenant"] = SomebodyElse,
            ["X-Ziggurat-Roles"] = "owner,operator"
        };
        var forged = new Dictionary<string, string>(plain)
        {
            ["X-Ziggurat-Assertion"] = TestAssertions.Of(new Account(SomebodyElse, SomebodyElse), SugaryChannel.ChannelId, ["owner", "operator"])
        };
        var agentApi = new Dictionary<string, string>(plain) { ["Authorization"] = $"Bearer {await AgentApiSecretAsync()}" };

        (await SmokeStack.FromInsideAsync(HttpMethod.Get, "http://mcp-sugary:8080/api/meals", plain)).Status
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await SmokeStack.FromInsideAsync(HttpMethod.Get, "http://mcp-sugary:8080/api/meals", forged)).Status
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await SmokeStack.FromInsideAsync(HttpMethod.Get, "http://observability:8080/", plain)).Status
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await SmokeStack.FromInsideAsync(HttpMethod.Get, "http://agent:8080/api/agents", agentApi)).Status
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The same headers through the proxy, with no session: the session check refuses before any app.
    [SkippableFact]
    public async Task SugaryAndTheDashboard_AreSignedOutForPlainIdentityHeaders()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        foreach (var (host, path) in new[] { (stack.SugaryHost, "/api/meals"), (stack.DashboardHost, "/") })
        {
            var request = stack.Request(HttpMethod.Get, host, path);
            request.Headers.Add("X-Ziggurat-User", SomebodyElse);
            request.Headers.Add("X-Ziggurat-Tenant", SomebodyElse);
            request.Headers.Add("X-Ziggurat-Roles", "owner,operator");

            (await stack.Http.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, host);
        }
    }

    // What the session check signs opens the app it was signed for, verified against the stack's own
    // accounts JWKS — and no other: the channel it names keeps Sugary's from opening the dashboard.
    [SkippableFact]
    public async Task AnAssertion_OpensTheAppItWasSignedFor_AndNoOther()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var forSugary = await AssertionAsync(stack.OperatorSession, SugaryChannel.ChannelId);
        var forDashboard = await AssertionAsync(stack.OperatorSession, AppChannels.Dashboard);
        var forSexyTime = await AssertionAsync(stack.OperatorSession, AppChannels.SexyTime);
        var agentSecret = await AgentApiSecretAsync();

        (await AsAsync("http://mcp-sugary:8080/api/meals", forSugary)).ShouldBe(HttpStatusCode.OK);
        (await AsAsync("http://observability:8080/", forDashboard)).ShouldBe(HttpStatusCode.OK);
        (await AsAsync("http://agent:8080/api/agents", forSexyTime, agentSecret)).ShouldBe(HttpStatusCode.OK);
        (await AsAsync("http://observability:8080/", forSugary)).ShouldBe(HttpStatusCode.Unauthorized);
        (await AsAsync("http://mcp-sugary:8080/api/meals", forDashboard)).ShouldBe(HttpStatusCode.Unauthorized);
        (await AsAsync("http://agent:8080/api/agents", forSugary, agentSecret)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    // A login handoff's page is on the browse server's pages listener, which Caddy reaches; its /mcp
    // is on the other listener, which nothing routed reaches, and neither listener answers the other's.
    [SkippableFact]
    public async Task TheBrowsePages_AreReachedThroughTheProxy_AndTheBrowseServersMcpIsNot()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var page = await SendAsync(HttpMethod.Get, stack.MainHost, $"/browse/handoff/{Guid.NewGuid():N}", accept: "text/html");

        page.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await page.Content.ReadAsStringAsync()).ShouldContain("This link has been used or has run out");
        (await SmokeStack.FromInsideAsync(HttpMethod.Get, $"http://{BrowseServerListeners.Service}:{BrowseServerListeners.Mcp}/browse/handoff/x"))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await SmokeStack.FromInsideAsync(HttpMethod.Post, $"http://{BrowseServerListeners.Service}:{BrowseServerListeners.Pages}/mcp"))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // Both issuers publish their keys on the compose network, where every verifier fetches them, and
    // nowhere a browser reaches: no host answers either JWKS, even to the operator.
    [SkippableFact]
    public async Task TheSigningKeys_ArePublishedInside_AndNeverThroughTheProxy()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        (await SmokeStack.FromInsideAsync(HttpMethod.Get, "http://accounts:8080/accounts/internal/jwks")).Body.ShouldContain("\"kty\"");
        (await SmokeStack.FromInsideAsync(HttpMethod.Get, "http://agent:8080/internal/jwks")).Body.ShouldContain("\"kty\"");

        var answers = await Task.WhenAll(stack.Hosts.SelectMany(host => new[] { "/accounts/internal/jwks", "/internal/jwks" }
            .Select(async path => (host, path,
                body: await (await SendAsync(HttpMethod.Get, host, path, stack.OperatorSession)).Content.ReadAsStringAsync()))));

        answers.Where(a => a.body.Contains("\"kty\"", StringComparison.Ordinal)).Select(a => $"{a.host}{a.path}").ShouldBeEmpty();
    }

    // The servers the agent dials on the deployment's network that this stack runs: every one gates its
    // tool calls on the agent's token (MCP__AUDIENCE in compose).
    private static async Task<IReadOnlyList<string>> GatedServersAsync()
    {
        var running = (await SmokeStack.DockerAsync("ps", "--filter", "label=com.docker.compose.project=jackbot-smoke",
            "--format", "{{.Names}}")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return DeploymentFiles.Compose().Services.Values
            .Where(s => s.Environment.ContainsKey("MCP__AUDIENCE") && s.Networks.Contains("jackbot") && running.Contains(s.Name))
            .Select(s => s.Name)
            .ToList();
    }

    // A tools/call as the agent would send it — the server's credential, the 2026-07-28 stateless
    // protocol's per-request metadata, and a conversation context naming a person's account — but
    // signed by nobody the server trusts. What comes back is the protocol's answer, refusal and all.
    private static async Task<string> ForgedCallAsync(string server, string credential, string? token)
    {
        var context = new ConversationContext("jonas", "smoke-forged", SomebodyElse, new ReplyTarget(ChannelIds.SignalR, "smoke-forged"),
            Principal: new Principal(ChannelIds.SignalR, SomebodyElse, new Account(SomebodyElse, SomebodyElse)));
        var meta = new JsonObject
        {
            [ChannelProtocol.ConversationContextMetaKey] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions),
            ["io.modelcontextprotocol/protocolVersion"] = ProtocolVersion,
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "smoke", ["version"] = "1" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
        };
        if (token is not null)
        {
            meta[ChannelProtocol.CallTokenMetaKey] = token;
        }

        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "fs_info", ["arguments"] = new JsonObject(), ["_meta"] = meta }
        };
        var answer = await SmokeStack.FromInsideAsync(HttpMethod.Post, $"http://{server}:8080/mcp", new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {credential}",
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json, text/event-stream",
            ["MCP-Protocol-Version"] = ProtocolVersion,
            ["Mcp-Method"] = "tools/call",
            ["Mcp-Name"] = "fs_info"
        }, call.ToJsonString());
        return $"{(int)answer.Status} {answer.Body}";
    }

    private static async Task<string> AgentApiSecretAsync() =>
        (await SmokeStack.DockerAsync("exec", "agent", "printenv", "AGENTAPI__SHAREDSECRET")).Trim();

    // The assertion the session check signs for a session on one channel, asked for as Caddy asks.
    private static async Task<string> AssertionAsync(string session, string channel) =>
        (await SmokeStack.FromInsideAsync(HttpMethod.Get, $"http://accounts:8080/accounts/verify?channel={channel}",
            new Dictionary<string, string> { ["Cookie"] = $"ziggurat_session={session}" }))
        .Headers[ForwardedAccount.AssertionHeader];

    private static async Task<HttpStatusCode> AsAsync(string url, string assertion, string? secret = null)
    {
        var headers = new Dictionary<string, string> { [ForwardedAccount.AssertionHeader] = assertion };
        if (secret is not null)
        {
            headers["Authorization"] = $"Bearer {secret}";
        }

        return (await SmokeStack.FromInsideAsync(HttpMethod.Get, url, headers)).Status;
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string host, string path, string? session = null, string? accept = null)
    {
        var request = stack.Request(method, host, path, session);
        if (accept is not null)
        {
            request.Headers.Add("Accept", accept);
        }

        return stack.Http.SendAsync(request);
    }
}