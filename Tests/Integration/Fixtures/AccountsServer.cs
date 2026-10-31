using System.Net;
using System.Text.RegularExpressions;
using Accounts;
using Accounts.Commands;
using Accounts.Internal;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
using Domain.DTOs.Channel;
using Domain.Security.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Integration.Fixtures;

// The accounts service booted through its shipped composition on a real port, with a database of
// its own in a directory of its own, and its clock in the test's hands: a link's week is advanced
// rather than waited. It is addressed as localhost, which a browser treats as a secure context, so
// a passkey ceremony runs against it as it would against the deployment.
public sealed partial class AccountsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly int _port;
    private readonly string _directory;

    private AccountsServer(WebApplication app, AccountsSettings settings, FakeTimeProvider clock, int port, string directory)
    {
        _app = app;
        Settings = settings;
        Clock = clock;
        _port = port;
        _directory = directory;
    }

    public AccountsSettings Settings { get; }

    public FakeTimeProvider Clock { get; }

    public Uri Address => new($"http://localhost:{_port}/");

    public static async Task<AccountsServer> StartAsync(Func<AccountsSettings, AccountsSettings>? configure = null)
    {
        var port = TestPort.GetAvailable();
        var directory = Path.Combine(Path.GetTempPath(), "accounts-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new AccountsSettings
        {
            DatabasePath = Path.Combine(directory, "accounts.db"),
            PublicBaseUrl = $"http://localhost:{port}",
            AllowedOrigins = [$"http://localhost:{port}"],
            WebAuthn = new WebAuthnSettings { RelyingPartyId = "localhost" }
        };
        settings = configure?.Invoke(settings) ?? settings;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddAccounts(settings, new AccountsClock(clock));
        var app = builder.Build();
        app.MapAccounts();
        await app.StartAsync();
        return new AccountsServer(app, settings, clock, port, directory);
    }

    public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    // A client that follows nothing and keeps no cookies, so each request says exactly what it carries.
    public HttpClient Client() =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = Address };

    // A signed-in browser's requests without the browser: the session cookie, and the antiforgery
    // cookie and token the service's pages hand out, carried as a browser would carry them.
    public PageClient SignedIn(string session) => new(Client(), session);

    // A verifier of the assertions this service signs, as the agent and the apps will hold one: over
    // the keys the service publishes, fetched from it, on the service's clock.
    public TokenVerifier AssertionVerifier() =>
        new(TokenIssuers.Accounts, new JwksCache(ct => Client().GetStringAsync(InternalApi.JwksPath, ct), Clock), Clock);

    public Task<TokenVerification<IdentityClaims>> VerifyAssertionAsync(string? assertion) =>
        AssertionVerifier().VerifyAsync(assertion, TokenAudiences.Assertion);

    // The container's command, run as `docker exec accounts dotnet Accounts.dll <args>` runs it.
    public async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        await using var output = new StringWriter();
        var exit = await AccountsCommand.RunAsync(args, Settings, new AccountsClock(Clock), output, CancellationToken.None);
        return (exit, output.ToString());
    }

    public async Task<MintedLink> MintOperatorAsync(string name)
    {
        var (exit, output) = await RunAsync(AccountsCommand.MintLink, "--operator", "--name", name);
        exit.ShouldBe(0, output);
        return MintedLink.Parse(output);
    }

    public async Task<string> MintSessionAsync(string userId)
    {
        var (exit, output) = await RunAsync(AccountsCommand.MintSession, "--user", userId);
        exit.ShouldBe(0, output);
        return output.Trim();
    }

    // What the proxy's forward_auth sends: a GET to the check carrying the browser's cookie and
    // Accept, and the original request's method, host and path in its forwarded headers.
    public Task<HttpResponseMessage> VerifyAsync(
        string? session, string method = "GET", string uri = "/", string accept = "text/html,application/xhtml+xml",
        string host = "assistants.example", string? role = null, string? channel = ChannelIds.SignalR)
    {
        var query = string.Join('&', new[] { ("role", role), ("channel", channel) }
            .Where(p => p.Item2 is not null)
            .Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));
        var request = new HttpRequestMessage(HttpMethod.Get, query.Length == 0 ? "/accounts/verify" : $"/accounts/verify?{query}");
        request.Headers.Add("X-Forwarded-Method", method);
        request.Headers.Add("X-Forwarded-Uri", uri);
        request.Headers.Add("X-Forwarded-Host", host);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");
        }

        return Client().SendAsync(request);
    }

    // A person the operator's command cannot make: a member of the home household, written straight
    // into the store as an owner's invite will write them.
    public async Task<string> SeedMemberAsync(string name)
    {
        await using var db = await Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        var home = await db.Tenants.SingleAsync(t => t.IsHome);
        var member = new User
        {
            Id = Tokens.NewId(), TenantId = home.Id, DisplayName = name, Role = Role.Member, CreatedAt = Clock.GetUtcNow()
        };
        db.Users.Add(member);
        await db.SaveChangesAsync();
        return member.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        TestPort.Release(_port);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    public sealed partial class PageClient(HttpClient client, string session)
    {
        private readonly Dictionary<string, string> _cookies = new() { [SessionCookie.Name] = session };

        public async Task<HttpResponseMessage> GetAsync(string path)
        {
            var response = await client.SendAsync(Request(HttpMethod.Get, path));
            Keep(response);
            return response;
        }

        public async Task<string> PageAsync(string path)
        {
            var response = await GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return await response.Content.ReadAsStringAsync();
        }

        // Posts a form the way the page at `from` would: with the antiforgery token it rendered.
        public async Task<HttpResponseMessage> PostAsync(string from, string action, params (string Name, string Value)[] fields)
        {
            var page = await PageAsync(from);
            var token = AntiforgeryField().Match(page);
            token.Success.ShouldBeTrue("the page renders no antiforgery token");
            return await PostWithoutPageAsync(action, [(token.Groups["name"].Value, token.Groups["value"].Value), .. fields]);
        }

        public async Task<HttpResponseMessage> PostWithoutPageAsync(string action, params (string Name, string Value)[] fields)
        {
            var request = Request(HttpMethod.Post, action);
            request.Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Name, f.Value)));
            var response = await client.SendAsync(request);
            Keep(response);
            return response;
        }

        private HttpRequestMessage Request(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            request.Headers.TryAddWithoutValidation("Accept", "text/html");
            return request;
        }

        private void Keep(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                return;
            }

            foreach (var pair in cookies.Select(c => c.Split(';')[0].Split('=', 2)).Where(p => p.Length == 2))
            {
                _cookies[pair[0]] = pair[1];
            }
        }

        [GeneratedRegex("""<input type="hidden" name="(?<name>[^"]+)" value="(?<value>[^"]+)">""")]
        private static partial Regex AntiforgeryField();
    }

    // Who a command says it made or minted for, in the words `mint-link` and `create-user` both use.
    public static (string UserId, string TenantId) ParseWho(string output)
    {
        var who = Who().Match(output);
        who.Success.ShouldBeTrue(output);
        return (who.Groups["user"].Value, who.Groups["tenant"].Value);
    }

    [GeneratedRegex(@"user (?<user>[0-9a-f]+), household (?<tenant>[0-9a-f]+)")]
    private static partial Regex Who();

    // What `mint-link` prints: who the link is for, and the link.
    public sealed record MintedLink(string UserId, string TenantId, Uri Url)
    {
        public string Token => Url.Segments[^1];

        public static MintedLink Parse(string output)
        {
            var (userId, tenantId) = ParseWho(output);
            var url = output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("http", StringComparison.Ordinal));
            return new MintedLink(userId, tenantId, new Uri(url));
        }
    }
}