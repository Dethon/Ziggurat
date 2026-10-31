using Domain.Contracts;
using Domain.Prompts;
using Domain.Security;
using Domain.Security.Signing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpClientManager : IAsyncDisposable
{
    // A tool server is dialled on the way into a session, so a server that is down is paid for by
    // the turn waiting behind it. The SDK's default is a minute; the retry below cannot shorten
    // that, because a handshake that times out is not the HttpRequestException it handles. The
    // handshake is one round trip against a server that has already accepted the connection, so a
    // server that is answering needs a fraction of this. Same bound, same reasoning, as the channel
    // connection's own dial.
    private static readonly TimeSpan _initializationTimeout = TimeSpan.FromSeconds(10);

    public IReadOnlyList<McpClient> Clients { get; }

    // The clients whose endpoint is a machine's (an outpost's tunnel). Discovery reads this: what such
    // a server says about itself is its own word, and which endpoints are machines is the agent's own
    // knowledge.
    public IReadOnlySet<McpClient> DynamicClients { get; }

    // The servers whose dial produced a client. An outpost whose dial was dropped is named absent by
    // these, never by its mount name, which a configured mount can coincidentally hold — the server
    // its tunnel was admitted as is the one thing that is the machine's own.
    public IReadOnlyList<ServerAddress> DialledServers { get; }

    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyList<PromptSection> Prompts { get; }

    // The skills the dialled servers ship, bound to their declarations. Read beside the prompts,
    // through the same cache, from every server that publishes an index.
    public IReadOnlyList<PromptSkill> Skills { get; }

    private bool _isDisposed;

    private McpClientManager(
        IReadOnlyList<McpClient> clients,
        IReadOnlySet<McpClient> dynamicClients,
        IReadOnlyList<ServerAddress> dialledServers,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<PromptSection> prompts,
        IReadOnlyList<PromptSkill> skills)
    {
        Clients = clients;
        DynamicClients = dynamicClients;
        DialledServers = dialledServers;
        Tools = tools;
        Prompts = prompts;
        Skills = skills;
    }

    public static async Task<McpClientManager> CreateAsync(
        string name,
        string userId,
        string description,
        IReadOnlyList<McpServerEndpoint> endpoints,
        McpClientHandlers handlers,
        McpPromptCache? promptCache = null,
        ILogger? logger = null,
        IReadImageStore? readImageStore = null,
        CallTokens? callTokens = null,
        CancellationToken ct = default)
    {
        var clientsWithEndpoints = await CreateClientsWithRetry(
            name, description, endpoints, handlers, callTokens, logger, ct);
        var toolsTask = LoadTools(clientsWithEndpoints, readImageStore, ct);
        var promptsTask = LoadPrompts(clientsWithEndpoints, userId, promptCache, ct);
        var skillsTask = LoadSkills(clientsWithEndpoints, promptCache, logger, ct);
        await Task.WhenAll(toolsTask, promptsTask, skillsTask);
        var clients = clientsWithEndpoints.Select(c => c.Client).ToArray();
        var dialled = clientsWithEndpoints.Select(c => c.Server).ToArray();
        var dynamicServers = endpoints
            .Where(e => e.Origin is McpEndpointOrigin.Dynamic)
            .Select(e => e.Server)
            .OfType<ServerAddress>()
            .ToHashSet();
        var dynamicClients = clientsWithEndpoints
            .Where(c => dynamicServers.Contains(c.Server))
            .Select(c => c.Client)
            .ToHashSet();
        return new McpClientManager(
            clients, dynamicClients, dialled, await toolsTask, await promptsTask, await skillsTask);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        foreach (var client in Clients)
        {
            await client.DisposeAsync();
        }
    }

    // Two rules, decided by the endpoint's origin and by nothing else. A configured endpoint is a
    // container in the deployment: it is retried and then, if it will not answer, it fails the
    // session. A dynamic one is an outpost — a machine whose tunnel is open — and its being
    // unreachable is normal — it is dialled once, logged and dropped, and its mount is simply absent for this
    // session. The temptation to unify these will recur; they are one implementation of two
    // different meanings of "not reachable", and the reasoning is recorded in
    // docs/adr/0027-static-endpoints-fail-dynamic-ones-are-dropped.md.
    //
    // A dynamic endpoint is dialled once rather than retried because the retry sleeps two, four and
    // eight seconds before giving up, and a laptop with its lid shut would charge that to every
    // session build, on the path a person is waiting on.
    //
    // The surviving clients keep the order they were given, which is what lets the caller put
    // configured endpoints first and have their mounts claim their names before any outpost does.
    private static async Task<(McpClient Client, ServerAddress Server, string Address)[]> CreateClientsWithRetry(
        string name,
        string description,
        IReadOnlyList<McpServerEndpoint> endpoints,
        McpClientHandlers handlers,
        CallTokens? callTokens,
        ILogger? logger,
        CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        var clients = await Task.WhenAll(endpoints.Select(async endpoint =>
        {
            Task<McpClient> dial() => McpClient.CreateAsync(
                Signed(TransportOf(endpoint), ServerOf(endpoint), callTokens),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = name, Description = description, Version = "1.0.0" },
                    Handlers = handlers,
                    InitializationTimeout = _initializationTimeout
                },
                cancellationToken: ct);

            if (endpoint.Origin is McpEndpointOrigin.Configured)
            {
                return ((McpClient Client, ServerAddress Server, string Address)?)
                    (await retryPolicy.ExecuteAsync(dial), ServerOf(endpoint), endpoint.Address);
            }

            try
            {
                return ((McpClient Client, ServerAddress Server, string Address)?)
                    (await dial(), ServerOf(endpoint), endpoint.Address);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The address is what names the machine: the server its tunnel was admitted as,
                // and the only thing here that identifies which one went away.
                logger?.LogWarning(ex,
                    "The outpost at {Endpoint} could not be dialled, so its mount is absent for this "
                    + "session; the next session build asks for the household's outposts again",
                    endpoint.Address);
                return null;
            }
        }));

        return [.. clients.Where(c => c is not null).Select(c => c!.Value)];
    }

    // Every server the agent dials is handed its call token on every tool call, outposts included,
    // and every one checks it — an outpost against the keys its tunnel delivered. A
    // manager built without tokens — a probe of what a server offers, which calls nothing — sends
    // none, and a gated server refuses its tool calls.
    internal static IClientTransport Signed(IClientTransport transport, ServerAddress server, CallTokens? callTokens) =>
        callTokens is null ? transport : new SigningClientTransport(transport, callTokens, TokenAudiences.CallTo(server));

    internal static IClientTransport Signed(IClientTransport transport, string address, CallTokens? callTokens) =>
        callTokens is null ? transport : new SigningClientTransport(transport, callTokens, TokenAudiences.CallTo(address));

    private static async Task<AITool[]> LoadTools(
        IEnumerable<(McpClient Client, ServerAddress Server, string Address)> clients,
        IReadImageStore? readImageStore,
        CancellationToken ct)
    {
        var tasks = clients.Select(async c =>
        {
            var tools = await c.Client.ListToolsAsync(cancellationToken: ct);
            return tools.Select(t => new QualifiedMcpTool(c.Server.Qualifier, t, readImageStore));
        });

        var results = await Task.WhenAll(tasks);
        return results
            .SelectMany(t => t)
            .Select(t => t.WithProgress(new Progress<ProgressNotificationValue>()))
            .ToArray<AITool>();
    }

    // A household's server reached through its tunnel is sent over the tunnel's own client, which
    // presents the tunnel's credential; everything else is dialled at its URL.
    private static HttpClientTransport TransportOf(McpServerEndpoint endpoint)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint.Address),
            // Presented on every request to a dialled server: its own credential. A server
            // reached through its tunnel is presented the tunnel's by the tunnel's client.
            AdditionalHeaders = endpoint.Secret is { } secret
                ? new Dictionary<string, string> { ["Authorization"] = SharedSecret.Header(secret) }
                : null
        };
        return endpoint.Through is { } through
            ? new HttpClientTransport(options, through, ownsHttpClient: false)
            : new HttpClientTransport(options);
    }

    // Read only once the endpoint's URL has been taken apart for its dial, which is what refuses one
    // that is not a URL — or a household's server nobody resolved.
    private static ServerAddress ServerOf(McpServerEndpoint endpoint) =>
        endpoint.Server
        ?? throw new UriFormatException(endpoint.NamesOwnServer
            ? $"The household server '{endpoint.Address}' was never resolved to its tunnel"
            : $"The MCP endpoint '{endpoint.Address}' is not an absolute URL");

    private static async Task<PromptSection[]> LoadPrompts(
        IEnumerable<(McpClient Client, ServerAddress Server, string Address)> clients,
        string userId,
        McpPromptCache? promptCache,
        CancellationToken ct)
    {
        var userContext = PromptManifest.Bind(
            PromptManifest.UserContext,
            $"## User Context\n" +
            $"Conversation created by user: '{userId}'\n" +
            $"Use this userId/username for all user-scoped operations. unless you get more " +
            $"updated information in the user's message");
        var perClient = await Task.WhenAll(clients
            .Where(c => c.Client.ServerCapabilities.Prompts is not null)
            .Select(c => promptCache is null
                ? FetchPromptsAsync(c.Client, ct)
                : promptCache.GetOrFetchAsync(c.Server.CacheKey, ctk => FetchPromptsAsync(c.Client, ctk), ct)));

        return perClient
            .SelectMany(p => p)
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Prepend(userContext)
            .ToArray();
    }

    // Every server that publishes resources may publish a skills index; one that does not ships no
    // skills. Outposts come through here like any other server, and bind under the undeclared
    // budgets like their prompts do. In dial order, so the advertisement is byte-stable across
    // sessions of one deployment.
    private static async Task<PromptSkill[]> LoadSkills(
        IEnumerable<(McpClient Client, ServerAddress Server, string Address)> clients,
        McpPromptCache? promptCache,
        ILogger? logger,
        CancellationToken ct)
    {
        var perClient = await Task.WhenAll(clients
            .Where(c => c.Client.ServerCapabilities.Resources is not null)
            .Select(c => ReadSkillsAsync(c.Client, c.Server, promptCache, logger, ct)));

        return [.. perClient.SelectMany(s => s)];
    }

    // The swallow sits here rather than in the reader: a server whose skills could not be read
    // ships none this session and the session still builds, but the failure reaches the cache
    // first, so nothing stores an empty list as though the server had answered with one.
    private static async Task<PromptSkill[]> ReadSkillsAsync(
        McpClient client, ServerAddress server, McpPromptCache? promptCache, ILogger? logger, CancellationToken ct)
    {
        try
        {
            return promptCache is null
                ? await McpSkillReader.ReadAsync(client, logger, ct)
                : await promptCache.GetOrFetchAsync(
                    server.CacheKey + ":skills", ctk => McpSkillReader.ReadAsync(client, logger, ctk), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "The skills of {Server} could not be read, so none of them are offered",
                client.ServerInfo?.Name);
            return [];
        }
    }

    // The name travels with the text. A server's prompt is words this repo did not write, so its
    // name is the only handle the manifest has on it: what it may cost, where it is read and what
    // it is for are declared against that name, and a prompt that arrives under a name nobody
    // declared is reported rather than silently budgeted.
    private static async Task<PromptSection[]> FetchPromptsAsync(McpClient client, CancellationToken ct)
    {
        var list = await client.ListPromptsAsync(cancellationToken: ct);
        return await Task.WhenAll(list.Select(async p =>
        {
            var result = await client.GetPromptAsync(p.Name, cancellationToken: ct);
            var text = string.Join("\n", result.Messages
                .Select(m => m.Content)
                .OfType<TextContentBlock>()
                .Select(t => t.Text));
            return PromptManifest.Bind(p.Name, text);
        }));
    }
}