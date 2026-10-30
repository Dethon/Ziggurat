using System.Net;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.WebChat;

namespace Tests.Smoke;

// What the deployment's compose file and Caddyfile promise, checked against a stack running them
// (scripts/smoke-stack.sh up, then test): who gets past the proxy, what the sandbox can reach, what is
// published, and one signed-in conversation with the real agent. The conformance tests read the files;
// these ask the running thing. Off unless ZIGGURAT_SMOKE=1, since they need that stack.
[Trait("Category", "Smoke")]
public sealed class StackSmokeTests(SmokeStack stack) : IClassFixture<SmokeStack>
{
    private const string SomebodyElse = "91eb477ecd34251c52fd4493523a0334";

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

        await Assertions.Expect(page.Locator(".chat-message.assistant .message-content").First)
            .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 180_000 });
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