using Domain.Outposts;
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

    // The addresses whose dial produced a client. The verdict writer reads this: an outpost whose
    // dial was dropped must not be judged by its name, which a configured mount can coincidentally
    // hold — the address is the one thing that is the machine's own.
    public IReadOnlyList<string> DialledEndpoints { get; }

    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyList<string> Prompts { get; }

    private bool _isDisposed;

    private McpClientManager(
        IReadOnlyList<McpClient> clients,
        IReadOnlyList<string> dialledEndpoints,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<string> prompts)
    {
        Clients = clients;
        DialledEndpoints = dialledEndpoints;
        Tools = tools;
        Prompts = prompts;
    }

    public static async Task<McpClientManager> CreateAsync(
        string name,
        string userId,
        string description,
        IReadOnlyList<McpServerEndpoint> endpoints,
        McpClientHandlers handlers,
        McpPromptCache? promptCache = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var clientsWithEndpoints = await CreateClientsWithRetry(
            name, description, endpoints, handlers, logger, ct);
        var toolsTask = LoadTools(clientsWithEndpoints, ct);
        var promptsTask = LoadPrompts(clientsWithEndpoints, userId, promptCache, ct);
        await Task.WhenAll(toolsTask, promptsTask);
        var clients = clientsWithEndpoints.Select(c => c.Client).ToArray();
        var dialled = clientsWithEndpoints.Select(c => c.Address).ToArray();
        return new McpClientManager(clients, dialled, await toolsTask, await promptsTask);
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
    // docs/adr/0027-static-endpoints-fail-dynamic-ones-are-dropped.md.
    //
    // A dynamic endpoint is dialled once rather than retried because the retry sleeps two, four and
    // eight seconds before giving up, and a laptop with its lid shut would charge that to every
    // session build, on the path a person is waiting on.
    //
    // The surviving clients keep the order they were given, which is what lets the caller put
    // configured endpoints first and have their mounts claim their names before any outpost does.
    private static async Task<(McpClient Client, string ServerName, string Address)[]> CreateClientsWithRetry(
        string name,
        string description,
        IReadOnlyList<McpServerEndpoint> endpoints,
        McpClientHandlers handlers,
        ILogger? logger,
        CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        var clients = await Task.WhenAll(endpoints.Select(async endpoint =>
        {
            Task<McpClient> dial() => McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(endpoint.Address),
                    // Presented on every request to a server that asked for one. Both directions
                    // present the same shared secret: the machine when it registers, and this when
                    // it dials the machine back.
                    AdditionalHeaders = endpoint.Secret is { } secret
                        ? new Dictionary<string, string> { ["Authorization"] = OutpostSecret.Header(secret) }
                        : null
                }),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = name, Description = description, Version = "1.0.0" },
                    Handlers = handlers,
                    InitializationTimeout = _initializationTimeout
                },
                cancellationToken: ct);

            if (endpoint.Origin is McpEndpointOrigin.Configured)
            {
                return ((McpClient Client, string ServerName, string Address)?)
                    (await retryPolicy.ExecuteAsync(dial), ExtractServerName(endpoint.Address), endpoint.Address);
            }

            try
            {
                return ((McpClient Client, string ServerName, string Address)?)
                    (await dial(), ExtractServerName(endpoint.Address), endpoint.Address);
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

        return [.. clients.Where(c => c is not null).Select(c => c!.Value)];
    }

    private static async Task<AITool[]> LoadTools(
        IEnumerable<(McpClient Client, string ServerName, string Address)> clients,
        CancellationToken ct)
    {
        var tasks = clients.Select(async c =>
        {
            var tools = await c.Client.ListToolsAsync(cancellationToken: ct);
            return tools.Select(t => new QualifiedMcpTool(c.ServerName, t));
        });

        var results = await Task.WhenAll(tasks);
        return results
            .SelectMany(t => t)
            .Select(t => t.WithProgress(new Progress<ProgressNotificationValue>()))
            .ToArray<AITool>();
    }

    private static string ExtractServerName(string endpoint)
    {
        var uri = new Uri(endpoint);
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}-{uri.Port}";
    }

    private static async Task<string[]> LoadPrompts(
        IEnumerable<(McpClient Client, string ServerName, string Address)> clients,
        string userId,
        McpPromptCache? promptCache,
        CancellationToken ct)
    {
        var userContextPrompt = $"## User Context\n" +
                                $"Conversation created by user: '{userId}'\n" +
                                $"Use this userId/username for all user-scoped operations. unless you get more " +
                                $"updated information in the user's message";
        var perClient = await Task.WhenAll(clients
            .Where(c => c.Client.ServerCapabilities.Prompts is not null)
            .Select(c => promptCache is null
                ? FetchPromptsAsync(c.Client, ct)
                : promptCache.GetOrFetchAsync(c.ServerName, ctk => FetchPromptsAsync(c.Client, ctk), ct)));

        return perClient
            .SelectMany(p => p)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Prepend(userContextPrompt)
            .ToArray();
    }

    private static async Task<string[]> FetchPromptsAsync(McpClient client, CancellationToken ct)
    {
        var list = await client.ListPromptsAsync(cancellationToken: ct);
        return await Task.WhenAll(list.Select(async p =>
        {
            var result = await client.GetPromptAsync(p.Name, cancellationToken: ct);
            return string.Join("\n", result.Messages
                .Select(m => m.Content)
                .OfType<TextContentBlock>()
                .Select(t => t.Text));
        }));
    }
}