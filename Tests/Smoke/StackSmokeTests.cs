using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accounts.Sessions;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Metrics;
using Domain.Monitor;
using Domain.Prompts;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.StateManagers;
using McpServerSugary.Channel;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.E2E.WebChat;
using Tests.Unit.Deployment;

namespace Tests.Smoke;

// What the deployment's compose file, its Caddyfile and the tenant kit promise, checked against a
// stack running them (scripts/smoke-stack.sh up, then test): who gets past the proxy, what the sandbox
// can reach, what is published, that what the issuers sign is what every server and app believes and
// nothing else is, that the home's servers are reached through their tunnels and nothing else and a
// tunnel opens for its own credential alone, and two signed-in conversations with the real agent, one
// through a tunnel. The conformance tests read the files; these ask the running thing. Off unless
// ZIGGURAT_SMOKE=1, since they need that stack.
[Trait("Category", "Smoke")]
public sealed class StackSmokeTests(SmokeStack stack) : IClassFixture<SmokeStack>
{
    private const string SomebodyElse = "91eb477ecd34251c52fd4493523a0334";
    private const string ProtocolVersion = "2026-07-28";

    // The schedule scripts/smoke-stack.sh up writes as it was kept before households.
    private const string BeforeHouseholds = "smoke-before-households";

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

    // The sandbox runs what the model writes. It is the kit's, on a network of its own: no name of
    // the deployment's resolves from it — the agent, and everything that believes the proxy's
    // identity headers — nor any of the kit's servers beside it. What it reaches of the agent is the
    // exec bridge, over its own tunnel from its back-call port, which answers an unsigned call 401.
    [SkippableFact]
    public async Task TheSandbox_ReachesTheAgentsBridgeThroughItsTunnel_AndNothingBesideIt()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var sandbox = await SmokeStack.KitContainerAsync("mcp-sandbox");
        string[] beside =
        [
            "agent", "accounts", "redis", "caddy", "mcp-sugary", "mcp-channel-signalr", "webui", "observability",
            "camoufox", "mcp-websearch", "mcp-vault", "mcp-library", "mcp-printer"
        ];
        var reached = await Task.WhenAll(beside.Select(async name =>
            (name, (await SmokeStack.TryDockerAsync("exec", sandbox, "getent", "hosts", name)).ExitCode)));
        var bridge = DeploymentFiles.KitCompose().Service("mcp-sandbox").Environment["VFSBRIDGEURL"];

        reached.Where(r => r.ExitCode == 0).Select(r => r.name).ShouldBeEmpty();
        (await SmokeStack.DockerAsync("exec", sandbox, "curl", "-sS", "-o", "/dev/null", "-w", "%{http_code}",
            "-X", "POST", $"{bridge}/attr?path=/")).ShouldBe("401");
    }

    // The home's servers are the kit's: each is connected through its tunnel, as the agent lists it,
    // and none is on the deployment's network, where nothing resolves their names.
    [SkippableFact]
    public async Task TheKitsServers_AreConnectedThroughTheirTunnels_AndNotOnTheDeploymentsNetwork()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        await KitConnectedAsync();
        var services = DeploymentFiles.KitCompose().Services.Values
            .Where(s => s.Environment.ContainsKey("MCP__TUNNEL__SERVERNAME"))
            .Select(s => s.Name);
        var resolved = await Task.WhenAll(services.Select(async name =>
            (name, (await SmokeStack.TryDockerAsync("exec", SmokeStack.Inside, "curl", "-sS", "--max-time", "5",
                $"http://{name}:8080/mcp")).Output)));

        // Not resolved either way: an unknown name falls through to the host's resolver, which may be
        // slow to say so — a lookup that runs out of time found no address either.
        resolved.Where(r => !r.Output.Contains("Could not resolve host", StringComparison.Ordinal)
                            && !r.Output.Contains("Resolving timed out", StringComparison.Ordinal))
            .Select(r => r.name).ShouldBeEmpty();
    }

    [SkippableFact]
    public async Task OnlyTheProxy_IsPublishedPastLoopback()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        // The kit's too: a tunnelled server needs no port, and the kit's Redis is on loopback.
        var ports = await Task.WhenAll(new[] { "jackbot-smoke", SmokeStack.KitProject }.Select(project =>
            SmokeStack.DockerAsync("ps", "--filter", $"label=com.docker.compose.project={project}",
                "--format", "{{.Names}} {{.Ports}}")));

        ports.SelectMany(p => p.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
    // the home household's agent by the name its configuration gives it and a real model, and the reply
    // back — then the conversation's topic listed by the hub and kept under the home, and the reply's
    // model calls counted under the home, whose turns the deployment's key pays for.
    [SkippableFact]
    public async Task ASignedInPerson_GetsAReplyFromTheHomesAgent_ItsTopicAndItsUsageUnderTheHome()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var since = DateTimeOffset.UtcNow;
        var tag = $"Smoke {Guid.NewGuid().ToString("N")[..4]}";
        var agent = HomeAgents().Single(a => a.Id == "jonas");

        await InChatAsync(agent.Name, async page =>
        {
            await SayAsync(page, $"{tag}: reply with one short sentence.");

            // Whatever comes back first, then that it is no error: a turn the agent refuses — its
            // assertion did not verify, a server refused its call token, the household has no key to
            // pay with — is answered too, as an error message with text in it.
            await Assertions.Expect(page.Locator(".chat-message.assistant .message-content").First)
                .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 180_000 });
            await Assertions.Expect(page.Locator(".chat-message.error")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".chat-message.assistant:not(.error) .message-content").First)
                .Not.ToBeEmptyAsync();

            // A page opened again holds nothing but what the hub lists for the person's household.
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.Locator(".topic-item", new PageLocatorOptions { HasText = tag }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        });

        (await stack.TopicsAsync(stack.Home, agent.Id)).ShouldContain(t => t.Name.StartsWith(tag));

        // The roll-ups key an agent by its id, which a household's rename leaves alone, and carry the
        // name it goes by (AgentSpec.MetricsAgentName), so the reply's own call is the configured
        // agent's on its configured model; the deployment's own scope holds none of the turn's calls,
        // though its key paid for them.
        await Eventually.Until(async () => (await UsageAsync(stack.Home, since))
                .Any(u => u.AgentId == agent.Id && u.AgentName == agent.Name && u.Model == agent.Model
                          && u.Sender == stack.MemberName),
            $"{agent.Name}'s reply on {agent.Model} counted under the home household");
        (await UsageAsync(MetricKeys.Deployment, since))
            .ShouldNotContain(u => u.Sender == stack.MemberName || u.AgentId == agent.Name || u.AgentId == agent.Id);
    }

    // The chat client offers the home household's agents by the names its configuration gives them —
    // jack and nabu exist only where the migration seeded the home's agents, since a household that
    // saved none runs the two templates.
    [SkippableFact]
    public async Task TheChatClient_OffersTheHomesAgents_ByTheNamesTheMigrationSeeded()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        await InChatAsync(agent: null, async page =>
        {
            var offered = page.Locator(".agent-seg");
            await Assertions.Expect(offered).ToHaveCountAsync(HomeAgents().Count);
            foreach (var (_, name, _) in HomeAgents())
            {
                await Assertions.Expect(offered.Filter(new LocatorFilterOptions { HasText = name })).ToHaveCountAsync(1);
            }
        });
    }

    // A schedule kept as it was before households is the home's after the migration: moved into the
    // home's own keys, due there, and offered as its writer by the agent's redelegate — which reads it
    // through the scheduling server, as every fire and listing does, and saves its delegation back.
    [SkippableFact]
    public async Task AScheduleFromBeforeHouseholds_IsKeptUnderTheHome_AndDelegatedAsItsWriter()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        (await stack.RedisAsync("SISMEMBER", RedisScheduleStore.HouseholdSetKey(stack.Home), BeforeHouseholds)).ShouldBe("1");
        (await stack.RedisAsync("ZSCORE", RedisScheduleStore.DueSetKey, RedisScheduleStore.DueMember(stack.Home, BeforeHouseholds)))
            .ShouldNotBeEmpty();
        var schedule = JsonNode.Parse(await stack.RedisAsync("GET", RedisScheduleStore.ScheduleKey(stack.Home, BeforeHouseholds)))!;
        schedule["Delegation"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        schedule["CreatedBy"]!["Account"]!["TenantId"]!.GetValue<string>().ShouldBe(stack.Home);

        (await stack.RedisAsync("EXISTS", $"schedule:{BeforeHouseholds}")).ShouldBe("0");
        (await stack.RedisAsync("SISMEMBER", "schedules", BeforeHouseholds)).ShouldBe("0");
    }

    // The same conversation through a household's own server: the agent reads a note from the kit's
    // vault, which it reaches through the vault's tunnel alone, and the word the note holds — written
    // here, so the model cannot know it otherwise — comes back in the reply. The call is signed for
    // the vault's synthetic address and checked against the keys the relay handed over that tunnel.
    [SkippableFact]
    public async Task ASignedInPerson_GetsAReplyThatReadsTheVaultThroughItsTunnel()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        await KitConnectedAsync();
        var tag = Guid.NewGuid().ToString("N")[..4];
        var word = $"smoke{Guid.NewGuid():N}"[..17];
        var note = $"smoke-{tag}.md";
        await File.WriteAllTextAsync(Path.Combine(KitVault, note), $"{word}\n");

        await InChatAsync(agent: "Jonas", async page =>
        {
            await SayAsync(page, $"Smoke {tag}: read the note {note} in the vault and reply with the one word written in it, nothing else.");

            await Assertions.Expect(page.Locator(".chat-message.assistant:not(.error) .message-content", new PageLocatorOptions { HasText = word }).First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 180_000 });
            await Assertions.Expect(page.Locator(".chat-message.error")).ToHaveCountAsync(0);
        });
    }

    // Somebody's own machine at the home: the outpost binary, run with its flags on the kit's machine,
    // opens its tunnel like any household server, and the home's Jonas — which uses outposts — reads
    // a file from it at the machine's own address, through that tunnel alone. The word is written here,
    // so the reply has it only from the machine.
    [SkippableFact]
    public async Task AMachineRunningTheOutpost_IsReadByTheAgentThroughItsTunnel()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        await Eventually.Until(async () => (await stack.ConnectedKitServersAsync()).Contains(SmokeStack.Outpost),
            "the kit's machine connected through its tunnel");
        var tag = Guid.NewGuid().ToString("N")[..4];
        var word = $"machine{Guid.NewGuid():N}"[..17];
        var file = $"smoke-{tag}.txt";
        await File.WriteAllTextAsync(Path.Combine(KitMachine, file), $"{word}\n");

        await InChatAsync(agent: "Jonas", async page =>
        {
            await SayAsync(page,
                $"Smoke {tag}: read the file /machine/{file} on the machine {SmokeStack.OutpostName} and reply with the one word written in it, nothing else.");

            await Assertions.Expect(page.Locator(".chat-message.assistant:not(.error) .message-content", new PageLocatorOptions { HasText = word }).First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 180_000 });
            await Assertions.Expect(page.Locator(".chat-message.error")).ToHaveCountAsync(0);
        });
    }

    // A household onboarded the whole way, as its operator and its owner would: created from the
    // dashboard, its owner signed in (mint-session standing in for the passkey their link enrolls), its
    // OpenRouter key set through the admin API — probed against the stack's stub, which takes it —
    // and its agent renamed there, then a reply from that agent under its new name, paid for with the
    // household's own key. That key is the deployment's, on this machine's stack alone, so the turn is
    // real and costs one cheap model call. Then the operator suspends it: its next turn on the same
    // open page is refused with the reason, and a page asked for again says it is suspended. Each run
    // makes a household of its own, so a rerun starts as fresh and needs nothing resumed.
    [SkippableFact]
    public async Task AHouseholdTheOperatorCreates_IsAnsweredByTheAgentItsOwnerNamed_UntilItIsSuspended()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        var tag = Guid.NewGuid().ToString("N")[..4];
        var (household, owner) = await KeyedHouseholdAsync(tag);

        var name = $"Smoke Butler {tag}";
        var agents = await AdminAsync<AdminSaved<AdminAgents>>(HttpMethod.Put, stack.MainHost,
            AdminProtocol.Agent(AgentTemplates.Jonas), owner, new AgentEdit(name, null));
        agents.Value.Agents.ShouldContain(a => a.Id == AgentTemplates.Jonas && a.Name == name);
        agents.Warning.ShouldBeNull();

        await InChatAsync(name, async page =>
        {
            await SayAsync(page, $"Smoke {tag}: reply with one short sentence.");
            await Assertions.Expect(page.Locator(".chat-message.assistant .message-content").First)
                .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 180_000 });
            await Assertions.Expect(page.Locator(".chat-message.error")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".chat-message.assistant:not(.error) .message-content").First)
                .Not.ToBeEmptyAsync();

            var suspended = await AdminAsync<AdminSaved<HouseholdSuspension>>(HttpMethod.Put, stack.DashboardHost,
                AdminProtocol.Suspension(household), stack.OperatorSession, new HouseholdSuspension(true));
            suspended.Value.Suspended.ShouldBeTrue();
            suspended.Warning.ShouldBeNull();

            await SayAsync(page, $"Smoke {tag}: and another.");
            await Assertions.Expect(page.Locator(".chat-message.error", new PageLocatorOptions { HasText = ConversationGroup.SuspendedAnswer }))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 60_000 });
        }, owner);

        var refused = await SendAsync(HttpMethod.Get, stack.MainHost, "/", owner, "text/html");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).ShouldContain(SessionCheck.SuspendedNotice);
    }

    // A tunnel is opened with the credential minted for that server and no other: a forged one, and
    // the vault's own presented as the printer's, are let through the proxy's route and refused by the
    // relay with the reason as the close — and the vault's own tunnel stands.
    [SkippableFact]
    public async Task AForgedTunnelCredential_IsRefusedAtTheTunnelRoute()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        await KitConnectedAsync();
        var vault = (await SmokeStack.DockerAsync("exec", await SmokeStack.KitContainerAsync("mcp-vault"),
            "printenv", "MCP__TUNNEL__CREDENTIAL")).Trim();

        (await TunnelClosedAsync("vault", "not-a-credential")).ShouldBe(
            (WebSocketCloseStatus.PolicyViolation, nameof(TunnelCredentialRefusal.Unknown)));
        (await TunnelClosedAsync("printer", vault)).ShouldBe(
            (WebSocketCloseStatus.PolicyViolation, nameof(TunnelCredentialRefusal.AnotherServer)));
        // Nor does a household server's credential make a machine of it: an outpost is one only because
        // its credential was minted for outpost-<NAME>.
        (await TunnelClosedAsync(SmokeStack.Outpost, vault)).ShouldBe(
            (WebSocketCloseStatus.PolicyViolation, nameof(TunnelCredentialRefusal.AnotherServer)));
        (await stack.ConnectedKitServersAsync()).ShouldContain("vault");
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
        // Every server the agent dials on the deployment's network that this stack starts, so a reader
        // regression cannot shrink "every" to one. The home's servers are the kit's (below).
        servers.ShouldBe(["mcp-idealista", "mcp-channel-signalr", "mcp-scheduling", "mcp-timers", "mcp-sugary"],
            ignoreOrder: true);

        var refusals = await Task.WhenAll(servers.Select(async server => await ForgedCallsAsync(
            server, (await SmokeStack.DockerAsync("exec", server, "printenv", "MCP__SHAREDSECRET")).Trim(),
            audience: server, SmokeStack.FromInsideAsync, $"http://{server}:8080/mcp")));

        ShouldAllBeRefused(refusals);
    }

    // The same forgeries at the kit's servers, from inside the kit's own network, presenting each
    // server's own tunnel credential and signed for its synthetic address: refused alike — and the
    // foreign key as unknown, not as no keys at all, which is what says the agent's keys reached each
    // server over its tunnel.
    [SkippableFact]
    public async Task AForgedCallFromInsideTheKitsNetwork_IsRefusedByEveryKitServer()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        await KitConnectedAsync();
        // The sandbox is on a network of its own, which no other container of the kit's is on.
        var servers = DeploymentFiles.KitCompose().Services.Values
            .Where(s => s.Environment.ContainsKey("MCP__TUNNEL__SERVERNAME") && s.Networks.Contains("kit"))
            .Select(s => (Service: s.Name, Name: s.Environment["MCP__TUNNEL__SERVERNAME"]))
            .Where(s => SmokeStack.KitServers.Contains(s.Name))
            .ToList();
        servers.Select(s => s.Name).ShouldBe(["websearch", "vault", "library", "printer"], ignoreOrder: true);

        var refusals = await Task.WhenAll(servers.Select(async server =>
        {
            var container = await SmokeStack.KitContainerAsync(server.Service);
            var credential = (await SmokeStack.DockerAsync("exec", container, "printenv", "MCP__TUNNEL__CREDENTIAL")).Trim();
            var port = server.Service == BrowseServerListeners.Service ? BrowseServerListeners.Mcp : 8080;
            return await ForgedCallsAsync(server.Service, credential, ServerAddress.Tunnelled(stack.Home, server.Name).Name,
                SmokeStack.FromInsideTheKitAsync, $"http://{server.Service}:{port}/mcp");
        }));

        ShouldAllBeRefused(refusals);
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

    // A login handoff's page is on the household's web search, at its own site: Caddy routes it to the
    // relay, which forwards it down the web search tunnel of the household its path names to its pages
    // listener, and the server's own answer comes back — a link nobody minted is gone, not the relay's offline page. Its /mcp is on
    // the other listener, which the pages tag never reaches, and neither listener answers the other's.
    [SkippableFact]
    public async Task TheBrowsePages_AnswerThroughTheTunnel_AndTheBrowseServersMcpIsNotOnTheirListener()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        await KitConnectedAsync();
        var page = await SendAsync(HttpMethod.Get, stack.MainHost, $"{BrowsePagePath.For(stack.Home)}/handoff/{Guid.NewGuid():N}", accept: "text/html");

        page.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await page.Content.ReadAsStringAsync()).ShouldContain("This link has been used or has run out");
        (await SmokeStack.FromInsideTheKitAsync(HttpMethod.Get, $"http://{BrowseServerListeners.Service}:{BrowseServerListeners.Mcp}{BrowsePagePath.For(stack.Home)}/handoff/x"))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await SmokeStack.FromInsideTheKitAsync(HttpMethod.Post, $"http://{BrowseServerListeners.Service}:{BrowseServerListeners.Pages}/mcp"))
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

    // A household's own vault, set up the way its owner would: a credential minted on its Servers
    // page (the admin route), a vault started at home with it — here a second kit vault beside the
    // home's — and the household's Jonas, from the template, reads a note through that vault's tunnel
    // on the household's own key. The home's vault holds a note of the same name with another word,
    // so a reply carrying the household's word read the household's vault and nobody else's.
    [SkippableFact]
    public async Task AHouseholdThatSetUpItsOwnVault_HasItsAgentReadItThroughItsOwnTunnel()
    {
        Skip.IfNot(SmokeStack.IsEnabled, "no smoke stack");

        await KitConnectedAsync();
        var tag = Guid.NewGuid().ToString("N")[..4];
        var (_, owner) = await KeyedHouseholdAsync(tag);
        var minted = await AdminAsync<AdminSaved<MintedServerCredential>>(HttpMethod.Post, stack.MainHost, AdminProtocol.Servers,
            owner, new ServerAddition("vault"));
        // No warning: the mint was announced, so the household's next turn reads the vault as set up.
        minted.Warning.ShouldBeNull();
        await SmokeStack.StartHouseholdVaultAsync(minted.Value.Credential);
        // Just started, so its tunnel opens within a container's start, asked through the owner's own
        // Servers page as they would watch it connect.
        await Eventually.Until(async () => (await AdminReadAsync<AdminServers>(stack.MainHost, AdminProtocol.Servers, owner))
                .Servers.Any(server => server is { Name: "vault", Connection: ServerConnection.Connected }),
            "the household's vault connected through its tunnel", TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2));

        var note = $"smoke-{tag}.md";
        var households = $"house{Guid.NewGuid():N}"[..17];
        var homes = $"home{Guid.NewGuid():N}"[..17];
        await File.WriteAllTextAsync(Path.Combine(HouseholdVault, note), $"{households}\n");
        await File.WriteAllTextAsync(Path.Combine(KitVault, note), $"{homes}\n");

        await InChatAsync(AgentTemplates.Named(AgentTemplates.Jonas)!.Name, async page =>
        {
            await SayAsync(page, $"Smoke {tag}: read the note {note} in the vault and reply with the one word written in it, nothing else.");

            await Assertions.Expect(page.Locator(".chat-message.assistant:not(.error) .message-content", new PageLocatorOptions { HasText = households }).First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 180_000 });
            await Assertions.Expect(page.Locator(".chat-message.error")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".chat-message", new PageLocatorOptions { HasText = homes })).ToHaveCountAsync(0);
        }, owner);
    }

    // A household made on the dashboard's operator route, its owner signed in (mint-session standing in
    // for the passkey their link enrolls) and its OpenRouter key set through the admin API — probed
    // against the stack's stub, which takes it. That key is the deployment's, on this machine's stack
    // alone, so its turns are real and cost a cheap model call each.
    private async Task<(string Household, string Owner)> KeyedHouseholdAsync(string tag)
    {
        var created = await AdminAsync<AdminSaved<OwnerLink>>(HttpMethod.Post, stack.DashboardHost, AdminProtocol.Households,
            stack.OperatorSession, new NewHousehold($"Smoke Household {tag}", $"Smoke Owner {tag}"));
        var owner = await SmokeStack.SessionOfAsync(created.Value.UserId);

        var keys = await AdminAsync<AdminSaved<HouseholdKeys>>(HttpMethod.Put, stack.MainHost, AdminProtocol.OpenRouterKey,
            owner, new OpenRouterKey(SmokeStack.DeploymentOpenRouterKey()));
        keys.Value.OpenRouter!.State.ShouldBe(KeyState.Working);
        keys.Value.NeedsOpenRouterKey.ShouldBeFalse();
        // No warning: the save was announced, so the agent reads the household with its key.
        keys.Warning.ShouldBeNull();
        return (created.Value.HouseholdId, owner);
    }

    // A call with no token, a garbage one, and one signed for the server by a key the running agent
    // does not publish.
    private static async Task<(string Server, string Missing, string Garbage, string ForeignKey)> ForgedCallsAsync(
        string server, string credential, string audience, Inside from, string url)
    {
        var signed = new TokenSigner(TestSigningKeys.Store(TestSigningKeys.Agent)).Sign(
            IdentityClaims.Person(SomebodyElse, SomebodyElse, ChannelIds.SignalR, ["owner"])
                .Stamped(TokenIssuers.Agent, audience, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2)));
        return (server,
            await ForgedCallAsync(from, url, credential, token: null),
            await ForgedCallAsync(from, url, credential, token: "not.a-token"),
            await ForgedCallAsync(from, url, credential, signed));
    }

    private static void ShouldAllBeRefused(IEnumerable<(string Server, string Missing, string Garbage, string ForeignKey)> refusals) =>
        refusals.ShouldSatisfyAllConditions(refusals.SelectMany(r => new Action[]
        {
            () => r.Missing.ShouldContain("Refused (Missing)", customMessage: r.Server),
            () => r.Garbage.ShouldContain("Refused (Malformed)", customMessage: r.Server),
            () => r.ForeignKey.ShouldContain("Refused (UnknownKey)", customMessage: r.Server)
        }).ToArray());

    // Every kit server the script starts is connected through its tunnel. The script started them
    // before the tests were built, so this is a check that they came up, not a wait for them to.
    private async Task KitConnectedAsync() =>
        await Eventually.Until(async () => SmokeStack.KitServers.All((await stack.ConnectedKitServersAsync()).Contains),
            $"the kit's {string.Join(", ", SmokeStack.KitServers)} connected through their tunnels");

    // The kit's vault on this machine (TenantKit/docker-compose.override.smoke.yml).
    private static string KitVault =>
        Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose", "volumes", "smoke", "kit", "vault");

    // What the kit's machine publishes through its outpost.
    private static string KitMachine =>
        Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose", "volumes", "smoke", "kit", "machine");

    // The second household's vault beside it (`scripts/smoke-stack.sh household-vault`).
    private static string HouseholdVault =>
        Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose", "volumes", "smoke", "kit", "household-vault");

    // Opens the tunnel route through Caddy as a household's server would, and says how the relay
    // closed it.
    private async Task<(WebSocketCloseStatus? Status, string? Reason)> TunnelClosedAsync(string server, string credential)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {credential}");
        socket.Options.SetRequestHeader(TunnelProtocol.ServerHeader, server);
        using var invoker = new HttpMessageInvoker(SmokeStack.Handler());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await socket.ConnectAsync(new Uri($"wss://{stack.MainHost}{TunnelProtocol.Path}"), invoker, timeout.Token);

        var buffer = new byte[4096];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer, timeout.Token);
        }
        while (received.MessageType != WebSocketMessageType.Close);

        return (socket.CloseStatus, socket.CloseStatusDescription);
    }

    // A signed-in browser through Caddy to the chat client, on a fresh topic of the agent named (or
    // whichever the client selects), as the member unless another session is given.
    private async Task InChatAsync(string? agent, Func<IPage, Task> converse, string? session = null)
    {
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
                Name = "ziggurat_session", Value = session ?? stack.MemberSession, Domain = stack.MainHost, Path = "/",
                Secure = true, HttpOnly = true
            }
        ]);
        var page = await context.NewPageAsync();

        await WebChatE2ETests.GotoWebChatAsync(page, $"https://{stack.MainHost}/");
        await WebChatE2ETests.SelectAgentAsync(page);
        if (agent is not null)
        {
            await WebChatE2ETests.ClickThroughApprovalsAsync(
                page, page.Locator(".agent-seg", new PageLocatorOptions { HasText = agent }));
            await Assertions.Expect(page.Locator(".agent-seg.on", new PageLocatorOptions { HasText = agent }))
                .ToBeVisibleAsync();
        }

        await converse(page);
    }

    private static async Task SayAsync(IPage page, string message)
    {
        var input = page.Locator("textarea.chat-input");
        await input.FillAsync(message);
        await input.PressAsync("Enter");
    }

    private delegate Task<InsideAnswer> Inside(
        HttpMethod method, string url, IReadOnlyDictionary<string, string>? headers, string? body);

    // A tools/call as the agent would send it — the server's credential, the 2026-07-28 stateless
    // protocol's per-request metadata, and a conversation context naming a person's account — but
    // signed by nobody the server trusts. What comes back is the protocol's answer, refusal and all.
    private static async Task<string> ForgedCallAsync(Inside from, string url, string credential, string? token)
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
        var answer = await from(HttpMethod.Post, url, new Dictionary<string, string>
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

    // The home's agents as the household migration seeds them (HouseholdMigration/Home/agents.json),
    // each a whole definition.
    private static IReadOnlyList<(string Id, string Name, string Model)> HomeAgents() =>
    [
        .. JsonNode.Parse(File.ReadAllText(Path.Combine(TestHelpers.FindSolutionRoot(), "HouseholdMigration", "Home", "agents.json")))!
            ["agents"]!.AsArray()
            .Select(entry => entry!["definition"]!)
            .Select(definition => (definition["id"]!.GetValue<string>(), definition["name"]!.GetValue<string>(),
                definition["model"]!.GetValue<string>()))
    ];

    // The model calls the dashboard's read API counts under a scope (a household's id, or the
    // deployment's own) since a moment, asked through the proxy as the operator.
    private async Task<IReadOnlyList<TokenUsageEvent>> UsageAsync(string scope, DateTimeOffset since)
    {
        var response = await SendAsync(HttpMethod.Get, stack.DashboardHost,
            $"/api/metrics/tokens?household={scope}&from={since.UtcDateTime:yyyy-MM-dd}", stack.OperatorSession);
        response.EnsureSuccessStatusCode();
        var events = JsonSerializer.Deserialize<List<TokenUsageEvent>>(
            await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return [.. events.Where(e => e.Timestamp >= since)];
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

    // An admin API call through the proxy as the admin client sends it: the session's cookie, the
    // admin header, a JSON body. A refusal fails the test with its status and AdminRefusal, which never
    // carries a value sent.
    private async Task<T> AdminAsync<T>(HttpMethod method, string host, string path, string session, object body)
    {
        var request = stack.Request(method, host, path, session);
        request.Headers.Add(AdminProtocol.RequestHeader, AdminProtocol.RequestHeaderValue);
        request.Headers.Add("Accept", "application/json");
        request.Content = JsonContent.Create(body, body.GetType(), options: AdminJson);
        using var response = await stack.Http.SendAsync(request);
        var answer = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {path} answered {(int)response.StatusCode}: {answer}");
        return JsonSerializer.Deserialize<T>(answer, AdminJson)!;
    }

    private async Task<T> AdminReadAsync<T>(string host, string path, string session)
    {
        var request = stack.Request(HttpMethod.Get, host, path, session);
        request.Headers.Add(AdminProtocol.RequestHeader, AdminProtocol.RequestHeaderValue);
        request.Headers.Add("Accept", "application/json");
        using var response = await stack.Http.SendAsync(request);
        var answer = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"GET {path} answered {(int)response.StatusCode}: {answer}");
        return JsonSerializer.Deserialize<T>(answer, AdminJson)!;
    }

    private static readonly JsonSerializerOptions AdminJson = new(JsonSerializerDefaults.Web);

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