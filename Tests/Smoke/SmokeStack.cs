using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.DTOs.WebChat;
using Domain.Outposts;
using Tests.E2E.Fixtures;
using Tests.Unit.Deployment;

namespace Tests.Smoke;

// A stack scripts/smoke-stack.sh started on this machine, reached as a browser reaches the deployment:
// by the deployment's own hostnames, through Caddy — but always at 127.0.0.1, whatever those names
// resolve to here, so a smoke run can never touch the deployment's host. The people it acts as are
// made in the stack's own accounts service for this run. Beside it runs the home household's tenant
// kit (project tenant-kit-smoke), its servers tunnelled to this stack's relay through Caddy.
public sealed partial class SmokeStack : IAsyncLifetime
{
    public const string Enabled = "ZIGGURAT_SMOKE";
    public const string KitProject = "tenant-kit-smoke";
    public const string Inside = "smoke-probe";
    private const int ProxyPort = 443;

    // The home's servers the script starts in the kit, by their tunnels' names: never voice or Home
    // Assistant's, which reach the home's satellites and devices.
    public static readonly IReadOnlyList<string> KitServers = ["websearch", "vault", "library", "sandbox", "printer"];

    // The kit's machine running the outpost (TenantKit/docker-compose.override.smoke.yml), by its name
    // and by the server name its tunnel credential was minted for.
    public const string OutpostName = "smoke-machine";
    public static readonly string Outpost = OutpostServerName.For(OutpostName);

    public static bool IsEnabled => Environment.GetEnvironmentVariable(Enabled) == "1";

    public string MainHost { get; } = HostStartingWith("assistants.");
    public string DashboardHost { get; } = HostStartingWith("assistants-dashboard.");
    public string SugaryHost { get; } = HostStartingWith("sugary.");
    public IReadOnlyList<string> Hosts { get; } =
        [.. DeploymentFiles.Caddyfile().Hosts.Select(h => h.Name).Where(n => !n.StartsWith('('))];

    public string OperatorSession { get; private set; } = "";
    public string MemberSession { get; private set; } = "";

    // The member's display name, which their turns' model calls are counted as sent by.
    public string MemberName { get; } = $"Smoke Member {Guid.NewGuid():N}"[..19];

    // The home household: the member is in it, and every kit server's credential was minted for it.
    public string Home { get; private set; } = "";

    public HttpClient Http { get; } = new(Handler());

    // Every name is answered at loopback: the deployment's hostnames select Caddy's site, and what they
    // resolve to on this machine is never asked.
    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ProxyPort), ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
        // Caddy's local CA, which nothing on this machine trusts.
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    };

    public async Task InitializeAsync()
    {
        if (!IsEnabled)
        {
            return;
        }

        var op = await DockerAsync("exec", "accounts", "dotnet", "Accounts.dll", "mint-link", "--operator", "--name", "Smoke Operator");
        OperatorSession = await SessionOfAsync(UserIn(op));
        var member = await DockerAsync("exec", "accounts", "dotnet", "Accounts.dll", "create-user", "--name", MemberName);
        MemberSession = await SessionOfAsync(UserIn(member));
        Home = HouseholdIn(member);
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

    // The home's servers connected through their tunnels now, as the agent's own listing says.
    public async Task<IReadOnlyList<string>> ConnectedKitServersAsync() =>
    [
        .. ConnectedServer()
            .Matches(await DockerAsync("exec", "agent", "dotnet", "Agent.dll", "list-household-servers", "--household", Home))
            .Select(m => m.Groups["name"].Value)
    ];

    // One command to the stack's Redis, as its own container's client asks (authenticated by the
    // container's REDISCLI_AUTH), and its answer trimmed.
    public async Task<string> RedisAsync(params string[] command) =>
        (await DockerAsync(["exec", "redis", "redis-cli", "--no-auth-warning", .. command])).Trim();

    // Every topic the thread store keeps for a household's agent, read off its household-keyed records.
    public async Task<IReadOnlyList<TopicMetadata>> TopicsAsync(string household, string agentId)
    {
        var keys = (await RedisAsync("--scan", "--pattern", $"topic@{household}:{agentId}:*"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var records = await Task.WhenAll(keys.Select(key => RedisAsync("GET", key)));
        return [.. records.Select(json => JsonSerializer.Deserialize<TopicMetadata>(json)!)];
    }

    public static Task<string> DockerAsync(params string[] args) => RunAsync("docker", args);

    // A program's output, or why it failed. The environment is laid over this process's, which is how
    // a secret reaches a script without a command line.
    private static async Task<string> RunAsync(
        string program, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = TestHelpers.FindSolutionRoot()
        };
        args.ToList().ForEach(start.ArgumentList.Add);
        (environment ?? new Dictionary<string, string>()).ToList().ForEach(pair => start.Environment[pair.Key] = pair.Value);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"{Path.GetFileName(program)} {string.Join(' ', args)}: {error}{output}");
    }

    // A second household's vault beside the kit's, on the credential its owner was just shown: the
    // script's own verb, so the service is the smoke override's and `down` removes it. The credential
    // goes by environment, never on a command line.
    public static Task StartHouseholdVaultAsync(string credential) =>
        RunAsync(Path.Combine(TestHelpers.FindSolutionRoot(), "scripts", "smoke-stack.sh"), ["household-vault"],
            new Dictionary<string, string> { ["SMOKE_HOUSEHOLD_VAULT_CREDENTIAL"] = credential });

    // A household's Lemonade forwarder in front of a stand-in box (`scripts/smoke-stack.sh
    // household-lemonade`, the credential in SMOKE_HOUSEHOLD_LEMONADE_CREDENTIAL), and the one model the
    // stand-in offers (TenantKit/docker-compose.override.smoke.yml).
    public static Task StartHouseholdLemonadeAsync(string credential) =>
        RunAsync(Path.Combine(TestHelpers.FindSolutionRoot(), "scripts", "smoke-stack.sh"), ["household-lemonade"],
            new Dictionary<string, string> { ["SMOKE_HOUSEHOLD_LEMONADE_CREDENTIAL"] = credential });

    public const string KitLemonadeModel = "smoke-kit-box";

    // The household's test satellite's Identity (TenantKit/docker-compose.override.smoke.yml), which a
    // guest's turn there goes by as its sender.
    public const string HouseholdSatellite = "Smoke household satellite";

    // A second household's own voice server beside the kit's, on the credential its owner was just
    // shown, and a test satellite of its own asking it aloud: the script's own verb, which starts them,
    // waits for the agent to follow that household's voice, and hears what the satellite recorded —
    // the sum and the timer ringing, or with refused the refusal of a household with no voice agent.
    // Its output is what the satellite heard; a check that failed throws with it.
    public static Task<string> AskHouseholdVoiceAsync(string credential, string household, bool refused) =>
        RunAsync(Path.Combine(TestHelpers.FindSolutionRoot(), "scripts", "smoke-stack.sh"),
            ["household-voice", household, .. refused ? new[] { "refused" } : []],
            new Dictionary<string, string> { ["SMOKE_HOUSEHOLD_VOICE_CREDENTIAL"] = credential });

    // A kit service's container, which compose names after the project: found by its labels.
    public static async Task<string> KitContainerAsync(string service) =>
        (await DockerAsync("ps", "--filter", $"label=com.docker.compose.project={KitProject}",
            "--filter", $"label=com.docker.compose.service={service}", "--format", "{{.Names}}")).Trim() is { Length: > 0 } name
            ? name
            : throw new InvalidOperationException($"the kit's {service} is not running");

    // A request made from inside the deployment's network, past Caddy, where a forged call would come
    // from: the smoke stack's probe container, beside every server and app.
    public static Task<InsideAnswer> FromInsideAsync(
        HttpMethod method, string url, IReadOnlyDictionary<string, string>? headers = null, string? body = null) =>
        FromAsync(Inside, method, url, headers, body);

    // The same from inside the kit's own network: from its browser container, which sits beside the
    // kit's servers and opens whatever page the model asks for. Its healthcheck is curl, so curl is there.
    public static async Task<InsideAnswer> FromInsideTheKitAsync(
        HttpMethod method, string url, IReadOnlyDictionary<string, string>? headers = null, string? body = null) =>
        await FromAsync(await KitContainerAsync("camoufox"), method, url, headers, body);

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

    private static async Task<InsideAnswer> FromAsync(
        string container, HttpMethod method, string url, IReadOnlyDictionary<string, string>? headers, string? body)
    {
        string[] args =
        [
            "exec", container, "curl", "-sS", "--max-time", "30", "-D", "-", "-X", method.Method,
            .. (headers ?? new Dictionary<string, string>()).SelectMany(h => new[] { "-H", $"{h.Key}: {h.Value}" }),
            .. body is null ? [] : new[] { "--data-binary", body },
            url
        ];
        // -D - writes the status line and the headers ahead of the body. Bodies here are small, so
        // curl never asks for a 100 Continue that would come before them.
        var parts = (await DockerAsync(args)).Split("\r\n\r\n", 2);
        var lines = parts[0].Split("\r\n");
        var answerHeaders = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return new InsideAnswer((HttpStatusCode)int.Parse(lines[0].Split(' ')[1]), answerHeaders,
            parts.Length > 1 ? parts[1] : "");
    }

    // The deployment's OpenRouter key on this machine, read where the agent reads it: the user secrets
    // its container mounts (DockerCompose/docker-compose.override.linux.yml), which win over the
    // environment. Only ever sent as a request's body, never put in a command line or a message.
    public static string DeploymentOpenRouterKey()
    {
        var id = SecretsId().Match(File.ReadAllText(Path.Combine(TestHelpers.FindSolutionRoot(), "Agent", "Agent.csproj")))
            .Groups["id"].Value;
        var secrets = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".microsoft", "usersecrets", id, "secrets.json");
        var key = File.Exists(secrets)
            ? JsonNode.Parse(File.ReadAllText(secrets))?["openRouter:apiKey"]?.GetValue<string>()
            : null;
        return key is { Length: > 0 }
            ? key
            : Environment.GetEnvironmentVariable("OPENROUTER__APIKEY") is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : throw new InvalidOperationException($"no OpenRouter key in the agent's user secrets ({secrets}) or OPENROUTER__APIKEY");
    }

    // Somebody new in the home household, signed in: a test that changes a person's standing makes its
    // own rather than touch the stack's.
    public static async Task<(string UserId, string Session)> HomePersonAsync(string name, string role)
    {
        var userId = UserIn(await DockerAsync("exec", "accounts", "dotnet", "Accounts.dll", "create-user", "--name", name, "--role", role));
        return (userId, await SessionOfAsync(userId));
    }

    public static async Task<string> SessionOfAsync(string userId) =>
        (await DockerAsync("exec", "accounts", "dotnet", "Accounts.dll", "mint-session", "--user", userId)).Trim()
            .Split('\n').Last().Trim();

    private static string UserIn(string commandOutput) =>
        UserId().Match(commandOutput) is { Success: true } match
            ? match.Groups["id"].Value
            : throw new InvalidOperationException($"no user id in: {commandOutput}");

    private static string HouseholdIn(string commandOutput) =>
        Household().Match(commandOutput) is { Success: true } match
            ? match.Groups["id"].Value
            : throw new InvalidOperationException($"no household in: {commandOutput}");

    private static string HostStartingWith(string prefix) =>
        DeploymentFiles.Caddyfile().Hosts.Select(h => h.Name).First(n => n.StartsWith(prefix, StringComparison.Ordinal));

    [GeneratedRegex("<UserSecretsId>(?<id>[^<]+)</UserSecretsId>")]
    private static partial Regex SecretsId();

    [GeneratedRegex(@"user (?<id>[0-9a-f]{32})")]
    private static partial Regex UserId();

    [GeneratedRegex(@"household (?<id>[0-9a-f]{32})")]
    private static partial Regex Household();

    // list-household-servers' line per connected server: its name, two spaces, since when.
    [GeneratedRegex(@"^(?<name>[a-z0-9-]+)  connected since", RegexOptions.Multiline)]
    private static partial Regex ConnectedServer();
}

public sealed record InsideAnswer(HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, string Body);