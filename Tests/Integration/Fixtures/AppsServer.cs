using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Accounts.Sessions;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Security;
using McpServerApps.Modules;
using McpServerApps.Serving;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Fixtures;

// The apps server booted through its shipped module, on a volume of its own, its households'
// agent names answered without an accounts service. One per test: what /apps lists is every app in
// the volume, so a sibling's apps would answer for this test's.
public sealed class AppsServer : IAsyncDisposable
{
    public const string AppsDomain = "apps.test";

    // Two people of one household, who keep their own apps, and one of another.
    public static readonly Account Her = new("household-home", "user-her");
    public static readonly Account Him = new("household-home", "user-him");
    public static readonly Account Neighbour = new("household-next-door", "user-neighbour");

    // The agents every household runs, as the accounts service's agent-names view lists them.
    public static readonly IReadOnlyList<HouseholdAgentName> HouseholdAgents =
        [new("jonas", "Jonas"), new("sugary", "Sugary")];

    private readonly WebApplication _app;
    private readonly McpClient _client;
    private readonly HttpClient _browser = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });

    private AppsServer(WebApplication app, McpClient client, AppsSettings settings, StandInSpaces spaces)
    {
        _app = app;
        _client = client;
        Settings = settings;
        Spaces = spaces;
    }

    // The households' spaces as the accounts service's spaces view would answer them: which apps each
    // shares as an app space. A test sets them, or makes them unreadable.
    public StandInSpaces Spaces { get; }

    public AppsSettings Settings { get; }

    public string Endpoint => $"http://localhost:{Settings.Listeners.Mcp}/mcp";

    // Where the proxy hands app origins: the second listener, which answers by Host and serves no /mcp.
    public string PagesAddress => $"http://127.0.0.1:{Settings.Listeners.Pages}";

    // The server's app sessions, which the app sign-in opens; a test may open its own.
    public AppSessions Sessions => _app.Services.GetRequiredService<AppSessions>();

    public BuiltAppStore Store => _app.Services.GetRequiredService<BuiltAppStore>();

    // Where the fixture's sessionless navigations are sent to sign in, unless a test names its own
    // accounts service.
    public const string MainHost = "https://assistants.test";

    // The server's clock: the system's, unless a test hands it the accounts service's.
    public TimeProvider Clock => _app.Services.GetRequiredService<TimeProvider>();

    // services: registrations over the shipped ones, as a test needs them (another clock, an accounts
    // service's own keys).
    // mcpPort: the MCP listener's, for a server another was told of before this one started (the accounts
    // service asks the listing there); a bind lost on it is retried elsewhere, as for any other.
    public static async Task<AppsServer> StartAsync(
        Func<AppsSettings, AppsSettings>? configure = null, Action<IServiceCollection>? services = null, int? mcpPort = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "apps-tests", Guid.NewGuid().ToString("N"));
        var spaces = new StandInSpaces();
        Directory.CreateDirectory(directory);
        AppsSettings? settings = null;
        // Both listeners are taken together, and a bind lost to either is retried on two fresh ports.
        var first = mcpPort;

        int Next()
        {
            if (first is not { } reserved)
            {
                return TestPort.GetAvailable();
            }

            first = null;
            return reserved;
        }

        var (_, app) = await TestPort.StartWebAsync(mcp =>
        {
            if (settings is not null)
            {
                TestPort.Release(settings.Listeners.Pages);
            }

            var configured = new AppsSettings
            {
                StoragePath = directory, AppsDomain = AppsDomain, Mcp = McpTestSecret.Gate, PublicBaseUrl = MainHost,
                Cdns = ["https://cdn.jsdelivr.net", "https://unpkg.com", "https://esm.sh"],
                Listeners = new AppListeners { Mcp = mcp, Pages = TestPort.GetAvailable() }
            };
            settings = configure?.Invoke(configured) ?? configured;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, settings.Listeners.Mcp);
                options.Listen(IPAddress.Loopback, settings.Listeners.Pages);
            });
            builder.Services.ConfigureApps(settings);
            builder.Services.Replace(ServiceDescriptor.Singleton<IHouseholdConfiguration>(
                new EveryHouseholdIn(TimeZoneInfo.Utc, HouseholdAgents, spaces: spaces.Read)));
            // The accounts service's test key, which a test's own accounts service signs with too.
            builder.Services.Replace(ServiceDescriptor.Singleton(sp => TestAssertions.Verifier(sp.GetRequiredService<TimeProvider>())));
            services?.Invoke(builder.Services);
            var built = builder.Build();
            built.MapMcp("/mcp");
            return built;
        }, Next);

        var client = await McpClient.CreateAsync(McpTestSecret.Transport($"http://localhost:{settings!.Listeners.Mcp}/mcp"));
        return new AppsServer(app, client, settings, spaces);
    }

    // The id a created app's note names, and the label its published origin and preview answer to.
    public static string IdIn(string note) => Regex.Match(note, @"\(id ([a-z0-9]{6})\)").Groups[1].Value;

    public string Published(string slug, string id) => $"{slug}-{id}.{Settings.AppsDomain}";

    public string Preview(string slug, string id) => $"{slug}-{id}{AppOrigin.PreviewSuffix}.{Settings.AppsDomain}";

    // A browser on an app origin, its own cookie for that origin sent when it holds a session there,
    // through the proxy, which says the browser's scheme was https.
    // signInState: the cookie the origin set as it sent the browser to sign in, which it holds the
    // callback to.
    public async Task<HttpResponseMessage> GetAsync(
        string host, string path, string? session = null, bool navigate = false, string? signInState = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PagesAddress + path);
        request.Headers.Host = host;
        request.Headers.Add("X-Forwarded-Proto", "https");
        var cookies = new[]
        {
            session is null ? null : $"{AppSessions.CookieName}={session}",
            signInState is null ? null : $"{AppViewers.StateCookieName}={signInState}"
        }.OfType<string>().ToList();
        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies));
        }

        if (navigate)
        {
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Accept", "text/html");
        }

        var response = await _browser.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    // A built app's page reading the household through the client library: a POST to the origin's
    // /_vfs/<operation> with the tool's own arguments, the app session's cookie and the page's Origin sent
    // as the browser sends them.
    // origin: another page's, as a sibling app's would send it.
    public async Task<HttpResponseMessage> ReadAsync(
        string host, string operation, JsonObject arguments, string? session, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PagesAddress}{AppReads.OriginPath}/{operation}")
        {
            Content = new StringContent(arguments.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Host = host;
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Origin", origin ?? $"https://{host}");
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{AppSessions.CookieName}={session}");
        }

        var response = await _browser.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    // The client library reporting a runtime error from a page of that origin: its JSON POSTed to the
    // origin's /_errors with the app session's cookie, the page's own Origin and its user agent, as the
    // browser sends them. origin: another page's, as a sibling app's would send it.
    public async Task<HttpResponseMessage> ReportAsync(
        string host, string body, string? session, string? origin = null, string userAgent = "Mozilla/5.0 (Linux; Android 15) Test")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PagesAddress + AppErrors.OriginPath)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Host = host;
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Origin", origin ?? $"https://{host}");
        request.Headers.UserAgent.ParseAdd(userAgent);
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{AppSessions.CookieName}={session}");
        }

        var response = await _browser.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    // The browser's round trip through a real accounts service up to its arrival back on the app's origin:
    // the app's redirect, the accounts service's answer to this person's session cookie. The callback's path
    // and query, and the state cookie the origin set on the way out.
    public async Task<(string Callback, string State)> SignInCallbackAsync(
        AccountsServer accounts, string host, string path, string session)
    {
        var toSignIn = await GetAsync(host, path, navigate: true);
        toSignIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var state = toSignIn.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().Split(';')[0]
            [(AppViewers.StateCookieName.Length + 1)..];
        using var request = new HttpRequestMessage(HttpMethod.Get, toSignIn.Headers.Location);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");
        request.Headers.Accept.ParseAdd("text/html");
        var back = await accounts.Client().SendAsync(request);
        back.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var callback = back.Headers.Location.ShouldNotBeNull();
        // The proxy's scheme, which the apps server names its origin by.
        callback.Scheme.ShouldBe("https");
        callback.Host.ShouldBe(host);
        return (callback.PathAndQuery, state);
    }

    // The whole app sign-in of a person signed in to that accounts service: the app session's token, the
    // cookie that set it and where the callback sent the browser on.
    public async Task<(string Token, string SetCookie, string Location)> SignInThroughAsync(
        AccountsServer accounts, string host, string path, string session)
    {
        var (callback, state) = await SignInCallbackAsync(accounts, host, path, session);
        var arrived = await GetAsync(host, callback, navigate: true, signInState: state);
        arrived.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var setCookies = arrived.Headers.GetValues("Set-Cookie").ToList();
        // The state is spent with the sign-in.
        setCookies.ShouldContain(c => c.StartsWith($"{AppViewers.StateCookieName}=;", StringComparison.Ordinal));
        var setCookie = setCookies.Single(c => c.StartsWith($"{AppSessions.CookieName}=", StringComparison.Ordinal));
        var token = setCookie.Split(';')[0][(AppSessions.CookieName.Length + 1)..];
        return (token, setCookie, arrived.Headers.Location.ShouldNotBeNull().OriginalString);
    }

    // An app session for this person on that origin, as the app sign-in would open one, its assertion
    // fresh.
    public string SessionFor(Account person, string host) =>
        Sessions.Open(person, "accounts-session-1", AppOrigin.Parse(host, Settings.AppsDomain)!,
            TestAssertions.Of(person, AppChannels.Apps, now: Clock.GetUtcNow(), sessionId: "accounts-session-1"),
            Clock.GetUtcNow().AddHours(1));

    // A chat-client turn of this person's on one of the household's agents, its principal as the hub
    // stamps it; the test transport signs the call for that person.
    public static ConversationContext TurnOf(Account person, string agentId = "jonas") => new(
        agentId, "conversation-1", person.UserId, new ReplyTarget("signalr", "conversation-1"),
        Principal: new Principal("signalr", person.UserId, person));

    // A tool called as the agent calls it on a turn; null calls it for nobody.
    public async Task<JsonNode> CallAsync(string tool, Dictionary<string, object?> arguments, ConversationContext? turn)
    {
        var result = await _client.CallToolAsync(new CallToolRequestParams
        {
            Name = tool,
            Arguments = arguments.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value)),
            Meta = turn is null
                ? null
                : new JsonObject
                {
                    [ChannelProtocol.ConversationContextMetaKey] =
                        JsonSerializer.SerializeToNode(turn, ChannelProtocol.SerializerOptions)
                }
        });
        return JsonNode.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)))!;
    }

    public Task<JsonNode> CreateAsync(Account person, string path, string content, bool overwrite = false) =>
        CallAsync("fs_create", new Dictionary<string, object?>
        {
            ["path"] = path, ["content"] = content, ["overwrite"] = overwrite, ["createDirectories"] = true
        }, TurnOf(person));

    public async Task<IReadOnlyList<string>> GlobAsync(Account person, string pattern, string basePath = "")
    {
        var glob = await CallAsync("fs_glob", new Dictionary<string, object?> { ["pattern"] = pattern, ["basePath"] = basePath },
            TurnOf(person));
        return glob["entries"]?.AsArray().Select(e => e!.GetValue<string>()).ToList()
               ?? throw new InvalidOperationException(glob.ToJsonString());
    }

    public Task<JsonNode> ExecAsync(Account person, string path, string command) =>
        CallAsync("fs_exec", new Dictionary<string, object?> { ["path"] = path, ["command"] = command }, TurnOf(person));

    public async Task<string> ReadAsync(Account person, string path)
    {
        var read = await CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = path }, TurnOf(person));
        return read["content"]?.GetValue<string>() ?? throw new InvalidOperationException(read.ToJsonString());
    }

    // An app as the agent creates one: its manifest, then nothing else.
    public async Task<string> AppAsync(Account person, string slug, string name = "Glucose today")
    {
        var created = await CreateAsync(person, $"/{slug}/app.json", Manifest(name));
        return created["note"]?.GetValue<string>() ?? throw new InvalidOperationException(created.ToJsonString());
    }

    public static string Manifest(string name = "Glucose today") =>
        $$"""{"name":"{{name}}","accentColor":"#e9601f","description":"My glucose today.","reads":["/sugary"]}""";

    // What the server keeps on its volume, for the claims a caller cannot see through the mount (a
    // repository and its metadata gone).
    public IReadOnlyList<string> StoredDirectories(Account household) =>
        Directory.Exists(Path.Combine(Settings.StoragePath, household.TenantId))
            ? Directory.GetDirectories(Path.Combine(Settings.StoragePath, household.TenantId))
            : [];

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await _client.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        TestPort.Release(Settings.Listeners.Mcp);
        TestPort.Release(Settings.Listeners.Pages);
        Directory.Delete(Settings.StoragePath, recursive: true);
    }
}

// Each household's spaces section, the one default space until a test says otherwise; Down makes every
// read fail as an accounts service that cannot be asked does.
public sealed class StandInSpaces
{
    private readonly ConcurrentDictionary<string, SpacesSection> _saved = new(StringComparer.Ordinal);

    public volatile bool Down;

    // Every read runs out of time, as the reader's HttpClient times out: a cancellation nobody asked for.
    public volatile bool TimesOut;

    // The household shares this app as an app space (beside its default space), named so.
    public void Share(string householdId, string appId, string name = "Glucose") =>
        _saved[householdId] = new SpacesSection
        {
            Spaces = [HouseholdSpace.Default, new HouseholdSpace { Slug = "shared", Name = name, AppId = appId, Order = 1 }]
        };

    public void Unshare(string householdId) => _saved.TryRemove(householdId, out _);

    public SpacesSection Read(string householdId) =>
        Down ? throw new HttpRequestException("the accounts service is down")
        : TimesOut ? throw new TaskCanceledException("the accounts service took too long", new TimeoutException())
        : _saved.GetValueOrDefault(householdId) ?? new SpacesSection();
}