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
    // A dial that produced a client: the server it reached, the address it was dialled at, and what
    // says that server went offline (McpServerEndpoint.Offline).
    private sealed record Dialled(McpClient Client, ServerAddress Server, string Address, CancellationToken Offline);

    // A tool server is dialled on the way into a session, so a server that is down is paid for by
    // the turn waiting behind it. The SDK's default is a minute; the retry below cannot shorten
    // that, because a handshake that times out is not the HttpRequestException it handles. The
    // handshake is one round trip against a server that has already accepted the connection, so a
    // server that is answering needs a fraction of this. Same bound, same reasoning, as the channel
    // connection's own dial.
    private static readonly TimeSpan _initializationTimeout = TimeSpan.FromSeconds(10);

    public IReadOnlyList<McpClient> Clients { get; }

    // The clients whose endpoint a machine registered at runtime. Discovery reads this: what such a
    // server says about itself is its own word, and who registered it is the hub's own knowledge.
    public IReadOnlySet<McpClient> DynamicClients { get; }

    // The addresses whose dial produced a client. The verdict writer reads this: an outpost whose
    // dial was dropped must not be judged by its name, which a configured mount can coincidentally
    // hold — the address is the one thing that is the machine's own.
    public IReadOnlyList<string> DialledEndpoints { get; }

    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyList<PromptSection> Prompts { get; }

    // The skills the dialled servers ship, bound to their declarations. Read beside the prompts,
    // through the same cache, from every server that publishes an index.
    public IReadOnlyList<PromptSkill> Skills { get; }

    private bool _isDisposed;

    private McpClientManager(
        IReadOnlyList<McpClient> clients,
        IReadOnlySet<McpClient> dynamicClients,
        IReadOnlyList<string> dialledEndpoints,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<PromptSection> prompts,
        IReadOnlyList<PromptSkill> skills)
    {
        Clients = clients;
        DynamicClients = dynamicClients;
        DialledEndpoints = dialledEndpoints;
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
        var dialled = clientsWithEndpoints.Select(c => c.Address).ToArray();
        var dynamicAddresses = endpoints
            .Where(e => e.Origin is McpEndpointOrigin.Dynamic)
            .Select(e => e.Address)
            .ToHashSet(StringComparer.Ordinal);
        var dynamicClients = clientsWithEndpoints
            .Where(c => dynamicAddresses.Contains(c.Address))
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
    // session. A dynamic one is a machine that registered itself, and its being unreachable is
    // normal — it is dialled once, logged and dropped, and its mount is simply absent for this
    // session. The temptation to unify these will recur; they are one implementation of two
    // different meanings of "not reachable", and the reasoning is recorded in
    // docs/adr/0027-static-endpoints-fail-dynamic-ones-are-dropped.md. A household server on its
    // tunnel is the one exception, recorded in .claude/rules/tunnel.md: it is left out when its tunnel
    // is gone, because a machine at home being off is not the deployment being broken.
    //
    // A dynamic endpoint is dialled once rather than retried because the retry sleeps two, four and
    // eight seconds before giving up, and a laptop with its lid shut would charge that to every
    // session build, on the path a person is waiting on.
    //
    // The surviving clients keep the order they were given, which is what lets the caller put
    // configured endpoints first and have their mounts claim their names before any outpost does.
    private static async Task<Dialled[]> CreateClientsWithRetry(
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

            Dialled dialled(McpClient client) => new(client, ServerOf(endpoint), endpoint.Address, endpoint.Offline);

            // A household server's tunnel is dialled once: an opening that failed its handshake is a
            // generation that is going, and waiting out the retry would not bring it back. Gone by the
            // time it answered, it is left out like a server found offline; failing any other way, it
            // is still a configured endpoint that will not answer.
            if (endpoint.Through is not null)
            {
                try
                {
                    return dialled(await dial());
                }
                catch (Exception ex) when (endpoint.Offline.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    logger?.LogWarning(ex,
                        "The household server {Endpoint} went offline while its session was being built, so its "
                        + "tools are absent this turn", endpoint.Address);
                    return null;
                }
            }

            if (endpoint.Origin is McpEndpointOrigin.Configured)
            {
                return dialled(await retryPolicy.ExecuteAsync(dial));
            }

            try
            {
                return dialled(await dial());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The address is what names the machine: it is what the outpost registered and
                // the only thing here that identifies which one went away.
                logger?.LogWarning(ex,
                    "The dynamically registered MCP endpoint {Endpoint} could not be dialled, so its "
                    + "mount is absent for this session; the next session build asks the registry again",
                    endpoint.Address);
                return null;
            }
        }));

        return [.. clients.OfType<Dialled>()];
    }

    // Every server the agent dials is handed its call token on every tool call, outposts included:
    // an outpost is not required to check it, and the agent does not ask which servers will. A
    // manager built without tokens — a probe of what a server offers, which calls nothing — sends
    // none, and a gated server refuses its tool calls.
    internal static IClientTransport Signed(IClientTransport transport, ServerAddress server, CallTokens? callTokens) =>
        callTokens is null ? transport : new SigningClientTransport(transport, callTokens, TokenAudiences.CallTo(server));

    internal static IClientTransport Signed(IClientTransport transport, string address, CallTokens? callTokens) =>
        callTokens is null ? transport : new SigningClientTransport(transport, callTokens, TokenAudiences.CallTo(address));

    private static async Task<AITool[]> LoadTools(
        IEnumerable<Dialled> clients,
        IReadImageStore? readImageStore,
        CancellationToken ct)
    {
        var tasks = clients.Select(async c =>
        {
            var tools = await c.Client.ListToolsAsync(cancellationToken: ct);
            return tools.Select(t => new QualifiedMcpTool(c.Server.Qualifier, t, readImageStore, c.Offline));
        });

        var results = await Task.WhenAll(tasks);
        return results
            .SelectMany(t => t)
            .Select(t => t.WithProgress(new Progress<ProgressNotificationValue>()))
            .ToArray<AITool>();
    }

    // A household's server reached through its tunnel is sent over the tunnel's own client, which
    // presents the tunnel's credential; everything else is dialled at its URL.
    internal static HttpClientTransport TransportOf(McpServerEndpoint endpoint)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint.Address),
            // Presented on every request to a server that asked for one. Both directions
            // present the same shared secret: the machine when it registers, and this when
            // it dials the machine back.
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
        IEnumerable<Dialled> clients,
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
        IEnumerable<Dialled> clients,
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