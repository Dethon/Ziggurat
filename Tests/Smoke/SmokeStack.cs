using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Tests.Unit.Deployment;

namespace Tests.Smoke;

// A stack scripts/smoke-stack.sh started on this machine, reached as a browser reaches the deployment:
// by the deployment's own hostnames, through Caddy — but always at 127.0.0.1, whatever those names
// resolve to here, so a smoke run can never touch the deployment's host. The people it acts as are
// made in the stack's own accounts service for this run.
public sealed partial class SmokeStack : IAsyncLifetime
{
    public const string Enabled = "ZIGGURAT_SMOKE";
    private const int ProxyPort = 443;

    public static bool IsEnabled => Environment.GetEnvironmentVariable(Enabled) == "1";

    public string MainHost { get; } = HostStartingWith("assistants.");
    public string DashboardHost { get; } = HostStartingWith("assistants-dashboard.");
    public string SugaryHost { get; } = HostStartingWith("sugary.");
    public IReadOnlyList<string> Hosts { get; } =
        [.. DeploymentFiles.Caddyfile().Hosts.Select(h => h.Name).Where(n => !n.StartsWith('('))];

    public string OperatorSession { get; private set; } = "";
    public string MemberSession { get; private set; } = "";
    public HttpClient Http { get; } = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // Every name is answered at loopback: the deployment's hostnames select Caddy's site, and
        // what they resolve to on this machine is never asked.
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ProxyPort), ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
        // Caddy's local CA, which nothing on this machine trusts.
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    });

    public async Task InitializeAsync()
    {
        if (!IsEnabled)
        {
            return;
        }

        var op = await DockerAsync("exec", "accounts", "dotnet", "Accounts.dll", "mint-link", "--operator", "--name", "Smoke Operator");
        OperatorSession = await SessionOfAsync(UserIn(op));
        var member = await DockerAsync("exec", "accounts", "dotnet", "Accounts.dll", "create-user", "--name", $"Smoke Member {Guid.NewGuid():N}"[..19]);
        MemberSession = await SessionOfAsync(UserIn(member));
    }

    public Task DisposeAsync()
    {
        Http.Dispose();
        return Task.CompletedTask;
    }

    public HttpRequestMessage Request(HttpMethod method, string host, string path, string? session = null)
    {
        var request = new HttpRequestMessage(method, $"https://{host}{path}");
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"ziggurat_session={session}");
        }

        return request;
    }

    public static async Task<string> DockerAsync(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        args.ToList().ForEach(start.ArgumentList.Add);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"docker {string.Join(' ', args)}: {error}{output}");
    }

    public static async Task<(int ExitCode, string Output)> TryDockerAsync(params string[] args)
    {
        try
        {
            return (0, await DockerAsync(args));
        }
        catch (InvalidOperationException ex)
        {
            return (1, ex.Message);
        }
    }

    private static async Task<string> SessionOfAsync(string userId) =>
        (await DockerAsync("exec", "accounts", "dotnet", "Accounts.dll", "mint-session", "--user", userId)).Trim()
            .Split('\n').Last().Trim();

    private static string UserIn(string commandOutput) =>
        UserId().Match(commandOutput) is { Success: true } match
            ? match.Groups["id"].Value
            : throw new InvalidOperationException($"no user id in: {commandOutput}");

    private static string HostStartingWith(string prefix) =>
        DeploymentFiles.Caddyfile().Hosts.Select(h => h.Name).First(n => n.StartsWith(prefix, StringComparison.Ordinal));

    [GeneratedRegex(@"user (?<id>[0-9a-f]{32})")]
    private static partial Regex UserId();
}