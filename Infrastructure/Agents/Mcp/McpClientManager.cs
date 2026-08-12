using Microsoft.Extensions.AI;
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
    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyList<string> Prompts { get; }

    private bool _isDisposed;

    private McpClientManager(
        IReadOnlyList<McpClient> clients,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<string> prompts)
    {
        Clients = clients;
        Tools = tools;
        Prompts = prompts;
    }

    public static async Task<McpClientManager> CreateAsync(
        string name,
        string userId,
        string description,
        string[] endpoints,
        McpClientHandlers handlers,
        McpPromptCache? promptCache = null,
        CancellationToken ct = default)
    {
        var clientsWithEndpoints = await CreateClientsWithRetry(name, description, endpoints, handlers, ct);
        var toolsTask = LoadTools(clientsWithEndpoints, ct);
        var promptsTask = LoadPrompts(clientsWithEndpoints, userId, promptCache, ct);
        await Task.WhenAll(toolsTask, promptsTask);
        var clients = clientsWithEndpoints.Select(c => c.Client).ToArray();
        return new McpClientManager(clients, await toolsTask, await promptsTask);
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

    private static async Task<(McpClient Client, string ServerName)[]> CreateClientsWithRetry(
        string name,
        string description,
        string[] endpoints,
        McpClientHandlers handlers,
        CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        var clients = await Task.WhenAll(endpoints.Select(async endpoint =>
        {
            var client = await retryPolicy.ExecuteAsync(() => McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = name, Description = description, Version = "1.0.0" },
                    Handlers = handlers,
                    InitializationTimeout = _initializationTimeout
                },
                cancellationToken: ct));

            var serverName = ExtractServerName(endpoint);
            return (client, serverName);
        }));

        return clients;
    }

    private static async Task<AITool[]> LoadTools(
        IEnumerable<(McpClient Client, string ServerName)> clients,
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
        IEnumerable<(McpClient Client, string ServerName)> clients,
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