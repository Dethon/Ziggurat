using System.Net;
using System.Text.RegularExpressions;
using Accounts;
using Accounts.Commands;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
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

    public static async Task<AccountsServer> StartAsync()
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
        string host = "assistants.example", string? role = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, role is null ? "/accounts/verify" : $"/accounts/verify?role={role}");
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

    // What `mint-link` prints: who the link is for, and the link.
    public sealed partial record MintedLink(string UserId, string TenantId, Uri Url)
    {
        public string Token => Url.Segments[^1];

        public static MintedLink Parse(string output)
        {
            var who = Who().Match(output);
            who.Success.ShouldBeTrue(output);
            var url = output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("http", StringComparison.Ordinal));
            return new MintedLink(who.Groups["user"].Value, who.Groups["tenant"].Value, new Uri(url));
        }

        [GeneratedRegex(@"user (?<user>[0-9a-f]+), household (?<tenant>[0-9a-f]+)")]
        private static partial Regex Who();
    }
}