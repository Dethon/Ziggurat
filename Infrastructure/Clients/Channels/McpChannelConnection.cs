using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Clients.Channels;

public sealed class McpChannelConnection(
    string channelId,
    bool attachOnly = false,
    ILogger<McpChannelConnection>? logger = null,
    // How often the run below asks whether the link is still there. The supervision cadence belongs
    // to the thing being supervised, which is why it moved here with the run.
    TimeSpan? healthCheckInterval = null)
    : IChannelConnection, IMcpChannelConnection, IAsyncDisposable
{
    private static readonly TimeSpan _defaultHealthCheckInterval = TimeSpan.FromSeconds(30);

    // A retry that has backed off this far is waiting on something that will not be fixed by
    // waiting longer, and the next attempt should not be minutes away.
    private static readonly TimeSpan _maxReconnectDelay = TimeSpan.FromSeconds(30);

    private const string CancelCommandContent = "/cancel";

    private static readonly TimeSpan _minBackoff = TimeSpan.FromSeconds(1);

    // Shared with the liveness contract, not a local tuning knob: ChannelInbox's freshness window
    // is sized to a fully held poll plus exactly one of these worst-case pauses, so raising the ceiling
    // here without raising the freshness window makes channel servers misread a retrying pump as
    // a disconnected agent.
    private static readonly TimeSpan _maxBackoff =
        TimeSpan.FromMilliseconds(ChannelProtocol.MaxReceiveRetryBackoffMs);

    // Long enough that only a poll the server never really held can miss it, short enough that
    // mistaking a genuinely short-waiting server for a spin costs milliseconds, not seconds.
    private static readonly TimeSpan _minHonouredWait =
        TimeSpan.FromMilliseconds(ChannelProtocol.DefaultReceiveWaitMs / 2.0);
    private static readonly TimeSpan _earlyEmptyThrottle = TimeSpan.FromMilliseconds(250);

    // One displaced poll is routine — our own reconnect issues a fresh poll right behind the dying
    // pump's, and the inbox retires the old waiter with an instant empty batch. A *run* of them is
    // the signature of another process polling the same subscriber id (two agents pointed at one
    // channel server — the dev/prod contention shape): the rival displaces us every time, and each
    // message reaches exactly one of the two processes, non-deterministically. Three in a row is
    // past anything a reconnect can produce; the re-warn interval keeps a long-lived rival visible
    // (~once a minute at the 250ms throttle) without turning sustained contention into log spam.
    private const int SplitStreamWarnThreshold = 3;
    private const int SplitStreamRewarnEvery = 240;

    private readonly Channel<ChannelMessage> _messageChannel = Channel.CreateUnbounded<ChannelMessage>();
    private McpClient? _client;
    // What the far end offers, for this connection generation and no other. Every server in this
    // repo registers its tools before its transport starts, so the answer cannot change while one
    // connection is up; a reconnect may be talking to a different process, so it starts over.
    // See docs/adr/0012-a-servers-tool-set-is-fixed-for-a-connection-generation.md.
    //
    // The answer carries the client it came from rather than being cleared when a generation ends:
    // a probe still in flight writes a tagged answer whenever it lands, and a later generation
    // simply does not recognise it. Clearing a bare field cannot say that — whichever order the
    // clear and the stale write happen in, one ordering leaves the old server's tools sitting in
    // the new connection's cache.
    //
    // Holding the old client past its disposal is what makes that tag readable, and it is the whole
    // cost: the field holds one answer, so a reconnect leaves at most one disposed client reachable
    // and the next fetch replaces it. Nulling the field here would not even remove that — a probe
    // still in flight writes its tagged answer afterwards either way — so it would buy nothing and
    // reintroduce the ordering the tag exists to avoid.
    private ToolSet? _tools;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    public string ChannelId { get; } = channelId;

    public bool AttachOnly { get; } = attachOnly;

    public IAsyncEnumerable<ChannelMessage> Messages => _messageChannel.Reader.ReadAllAsync();

    // Connect, register the catalog, watch health, reconnect, re-register — for as long as the
    // token lives. The order is here rather than in a caller because the order is about this.
    public async Task RunAsync(
        string endpoint, Func<IReadOnlyList<AgentCatalogEntry>> catalog, CancellationToken ct)
    {
        var interval = healthCheckInterval ?? _defaultHealthCheckInterval;
        try
        {
            await WithRetryAsync(endpoint, reconnect: false, ct);
            var entries = catalog();
            var registered = await TryRegisterAgentsAsync(entries, ct);
            var lastRegistered = registered ? Fingerprint(entries) : null;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                entries = catalog();

                if (!await IsHealthyAsync(ct))
                {
                    logger?.LogWarning("Channel {ChannelId} health check failed, reconnecting", ChannelId);
                    await WithRetryAsync(endpoint, reconnect: true, ct);
                    registered = await TryRegisterAgentsAsync(entries, ct);
                }
                else if (!registered)
                {
                    // The link is healthy but a previous registration failed; retry until it sticks
                    // so the channel is not left serving an empty catalog indefinitely.
                    registered = await TryRegisterAgentsAsync(entries, ct);
                }
                else if (Fingerprint(entries) != lastRegistered)
                {
                    // The catalog is not constant: attachment capability is discovered from the
                    // model provider and refreshed hourly. A model that gains image support has to
                    // reach the channels without a restart, and re-registering is how the channel
                    // already learns a catalog.
                    logger?.LogInformation(
                        "The agent catalog changed; re-registering it with channel {ChannelId}", ChannelId);
                    registered = await TryRegisterAgentsAsync(entries, ct);
                }
                else
                {
                    continue;
                }

                lastRegistered = registered ? Fingerprint(entries) : lastRegistered;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down. A call in flight when the token trips reports the abort it is, and
            // this is where the run ends: it returns rather than faulting its caller.
        }
    }

    // One loop for both, because a reconnect is a connect that had a predecessor: a fix to the
    // back-off could otherwise land in one and miss the other.
    private async Task WithRetryAsync(string endpoint, bool reconnect, CancellationToken ct)
    {
        var verb = reconnect ? "Reconnecting" : "Connecting";
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                logger?.LogInformation(
                    "{Verb} channel {ChannelId} to {Endpoint} (attempt {Attempt})",
                    verb, ChannelId, endpoint, attempt);
                if (reconnect)
                {
                    await ReconnectAsync(endpoint, ct);
                }
                else
                {
                    await ConnectAsync(endpoint, ct);
                }
                logger?.LogInformation("Channel {ChannelId} connected", ChannelId);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Includes an OperationCanceledException whose token is not ours: HttpClient
                // reports its own timeouts as TaskCanceledException, and letting that shape
                // escape here faults the run and stops the whole host.
                var delay = TimeSpan.FromSeconds(
                    Math.Min(Math.Pow(2, attempt), _maxReconnectDelay.TotalSeconds));
                logger?.LogWarning(
                    "Failed {Verb} channel {ChannelId} (attempt {Attempt}), retrying in {Delay}s: {Error}",
                    verb.ToLowerInvariant(), ChannelId, attempt, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, ct);
            }
        }

        // Returning from here would say "connected" to a caller that goes on to register and poll.
        // The only way out of the loop other than a successful dial is the token, so say that.
        ct.ThrowIfCancellationRequested();
    }

    // Value equality over a catalog whose members are lists, which records compare by reference.
    // The wire shape is the comparison that matters anyway: two catalogs that serialize the same
    // are the same as far as the channel is concerned.
    private static string Fingerprint(IReadOnlyList<AgentCatalogEntry> agents) =>
        JsonSerializer.Serialize(agents, ChannelProtocol.SerializerOptions);

    private async Task<bool> TryRegisterAgentsAsync(
        IReadOnlyList<AgentCatalogEntry> agents, CancellationToken ct)
    {
        try
        {
            await RegisterAgentsAsync(agents, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Failed to register agents with channel {ChannelId}: {Error}", ChannelId, ex.Message);
            return false;
        }
    }

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        // Never leave a second pump alive on this connection: two of them share one subscriberId
        // and displace each other's waiter, which the inbox retires with an instant empty batch.
        await StopPumpAsync();

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = $"{ChannelProtocol.ChannelClientNamePrefix}{ChannelId}",
                    Version = "1.0.0"
                }
            },
            cancellationToken: ct);

        _client = client;
        _pumpCts = new CancellationTokenSource();
        // The pump runs against the client of its own generation, handed to it rather than read
        // back off the field: a reconnect nulls the field, and this loop must not see that.
        _pumpTask = PumpAsync(client, _pumpCts.Token);
    }

    // Inbound items are pulled, not pushed: a stateless server cannot address a session, so the
    // agent long-polls channel_receive and feeds the two notification handlers itself.
    private async Task PumpAsync(McpClient client, CancellationToken ct)
    {
        var subscriberId = $"{ChannelProtocol.ChannelClientNamePrefix}{ChannelId}";
        var backoff = _minBackoff;
        var consecutiveEarlyEmpties = 0;

        while (!ct.IsCancellationRequested)
        {
            TimeSpan pause;
            try
            {
                if (await PollOnceAsync(client, subscriberId, ct))
                {
                    // Items delivered, or the wait honoured in full: re-poll at once, so the next
                    // real message is never sitting behind a timer.
                    backoff = _minBackoff;
                    consecutiveEarlyEmpties = 0;
                    continue;
                }

                consecutiveEarlyEmpties++;
                if (consecutiveEarlyEmpties == SplitStreamWarnThreshold ||
                    consecutiveEarlyEmpties % SplitStreamRewarnEvery == 0)
                {
                    logger?.LogWarning(
                        "{Tool} on {ChannelId} was displaced {Count} polls in a row; another process " +
                        "is likely polling subscriber id {SubscriberId} and splitting the stream — " +
                        "each message reaches exactly one of the two processes",
                        ChannelProtocol.ReceiveTool, ChannelId, consecutiveEarlyEmpties, subscriberId);
                }
                else
                {
                    logger?.LogDebug(
                        "{Tool} came back empty early on {ChannelId}; throttling the next poll",
                        ChannelProtocol.ReceiveTool, ChannelId);
                }

                pause = _earlyEmptyThrottle;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "channel_receive failed on {ChannelId}; retrying", ChannelId);
                pause = backoff;
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, _maxBackoff.TotalSeconds));
                // An error is not displacement evidence; a rival poller produces clean empties.
                consecutiveEarlyEmpties = 0;
            }

            try
            {
                await Task.Delay(pause, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // False when the batch came back empty without the server having honoured a meaningful share
    // of the wait. That is the shape of a displaced waiter, which the inbox retires instantly —
    // re-polling on it with no pause is how a stray second poller spins a core.
    private async Task<bool> PollOnceAsync(McpClient client, string subscriberId, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var call = await client.CallToolAsync(
            ChannelProtocol.ReceiveTool,
            new Dictionary<string, object?>
            {
                ["subscriberId"] = subscriberId,
                ["maxWaitMs"] = ChannelProtocol.DefaultReceiveWaitMs
            },
            cancellationToken: ct);

        var text = call.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        if (call.IsError == true || string.IsNullOrEmpty(text))
        {
            // Not a batch: re-polling straight away would spin the loop hot against a server that
            // keeps answering this way, so take the back-off path.
            throw new InvalidOperationException($"{ChannelProtocol.ReceiveTool} returned no batch: {text}");
        }

        var batch = JsonSerializer.Deserialize<ChannelReceiveResult>(text, ChannelProtocol.SerializerOptions);
        var items = batch?.Items ?? [];
        foreach (var item in items)
        {
            Dispatch(item);
        }

        return items.Count > 0 || Stopwatch.GetElapsedTime(startedAt) >= _minHonouredWait;
    }

    private void Dispatch(ChannelInboxItem item)
    {
        if (item.Kind == ChannelInboxItemKind.Message)
        {
            HandleChannelMessageNotification(
                JsonSerializer.SerializeToElement(item.Message, ChannelProtocol.SerializerOptions));
        }
        else
        {
            HandleChannelCancelNotification(
                JsonSerializer.SerializeToElement(item.Cancel, ChannelProtocol.SerializerOptions));
        }
    }

    private async Task StopPumpAsync()
    {
        if (_pumpCts is null)
        {
            return;
        }

        await _pumpCts.CancelAsync();
        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pumpCts.Dispose();
        _pumpCts = null;
        _pumpTask = null;
    }

    public void HandleChannelMessageNotification(JsonElement payload)
    {
        ChannelMessageNotification? notification;
        try
        {
            notification = ChannelProtocol.Deserialize<ChannelMessageNotification>(payload);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Discarding malformed channel/message notification on {ChannelId}", ChannelId);
            return;
        }

        if (notification is null)
        {
            return;
        }

        var message = new ChannelMessage
        {
            ConversationId = notification.ConversationId,
            Content = notification.Content,
            Sender = notification.Sender,
            ChannelId = ChannelId,
            AgentId = notification.AgentId,
            ReplyTo = notification.ReplyTo,
            Origin = notification.Origin,
            Location = notification.Location,
            SatelliteId = notification.SatelliteId,
            DismissedAlert = notification.DismissedAlert,
            ConfigPatch = notification.ConfigPatch,
            TurnKey = notification.TurnKey
        };

        _messageChannel.Writer.TryWrite(message);
    }

    public void HandleChannelCancelNotification(JsonElement payload)
    {
        ChannelCancelNotification? notification;
        try
        {
            notification = ChannelProtocol.Deserialize<ChannelCancelNotification>(payload);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Discarding malformed channel/cancel notification on {ChannelId}", ChannelId);
            return;
        }

        if (notification is null)
        {
            return;
        }

        var message = new ChannelMessage
        {
            ConversationId = notification.ConversationId,
            Content = CancelCommandContent,
            Sender = ChannelProtocol.SystemSender,
            ChannelId = ChannelId,
            AgentId = notification.AgentId
        };

        _messageChannel.Writer.TryWrite(message);
    }

    public async Task SendReplyAsync(SendReplyParams reply, CancellationToken ct)
    {
        var client = RequireClient();
        // send_reply fires once per streamed content chunk (hundreds per response). Building
        // the args dictionary directly avoids ChannelProtocol.ToArguments's reflection
        // SerializeToDocument + per-property Clone on the hot path; the wire JSON is
        // identical (same camelCase keys, ContentType.ToString() matches the
        // JsonStringEnumConverter output).
        await client.CallToolAsync(
            ChannelProtocol.SendReplyTool,
            new Dictionary<string, object?>
            {
                ["conversationId"] = reply.ConversationId,
                ["content"] = reply.Content,
                ["contentType"] = reply.ContentType.ToString(),
                ["isComplete"] = reply.IsComplete,
                ["messageId"] = reply.MessageId,
                ["turnKey"] = reply.TurnKey,
                ["agentInitiated"] = reply.AgentInitiated
            },
            cancellationToken: ct);
    }

    public async Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct)
    {
        var client = RequireClient();
        var result = await client.CallToolAsync(
            ChannelProtocol.RequestApprovalTool,
            ChannelProtocol.ToArguments(new RequestApprovalParams
            {
                ConversationId = conversationId,
                Mode = ApprovalMode.Request,
                Requests = requests
            }),
            cancellationToken: ct);

        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        return Enum.TryParse<ToolApprovalResult>(text, ignoreCase: true, out var parsed)
            ? parsed
            : ToolApprovalResult.Rejected;
    }

    public async Task NotifyAutoApprovedAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct)
    {
        var client = RequireClient();
        await client.CallToolAsync(
            ChannelProtocol.RequestApprovalTool,
            ChannelProtocol.ToArguments(new RequestApprovalParams
            {
                ConversationId = conversationId,
                Mode = ApprovalMode.Notify,
                Requests = requests
            }),
            cancellationToken: ct);
    }

    public async Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        string? initialPrompt,
        string? address,
        string? existingConversationId,
        CancellationToken ct)
    {
        var client = _client;
        if (client is null)
        {
            return null;
        }

        try
        {
            if (!await OffersToolAsync(client, ChannelProtocol.CreateConversationTool, ct))
            {
                return null;
            }

            var result = await client.CallToolAsync(
                ChannelProtocol.CreateConversationTool,
                new Dictionary<string, object?>
                {
                    ["agentId"] = agentId,
                    ["topicName"] = topicName,
                    ["sender"] = sender,
                    ["initialPrompt"] = initialPrompt,
                    ["address"] = address,
                    ["existingConversationId"] = existingConversationId
                },
                cancellationToken: ct);

            // A rejected create (e.g. unknown voice satellite) comes back as IsError with the
            // error text as content; treat it as "no conversation" rather than a conversation id.
            if (result.IsError == true)
            {
                return null;
            }

            return result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        }
        catch (McpException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A reconnect disposes the client under a create still in flight, which cancels the
            // transport's own token — not the caller's. That is "not connected", and not connected
            // returns null here (ADR 0011); an abort the caller asked for still propagates.
            return null;
        }
    }

    public async Task RegisterAgentsAsync(IReadOnlyList<AgentCatalogEntry> agents, CancellationToken ct)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        if (!await OffersToolAsync(client, ChannelProtocol.RegisterAgentsTool, ct))
        {
            return;
        }

        var result = await client.CallToolAsync(
            ChannelProtocol.RegisterAgentsTool,
            ChannelProtocol.ToArguments(new RegisterAgentsParams { Agents = agents }),
            cancellationToken: ct);

        // A refused registration reaches the caller as a value, not a throw: the channel-side
        // call-tool error filter turns every non-cancellation exception into an IsError result.
        // Failing here is what keeps the run retrying — swallowing it latches "registered" on a
        // catalog the channel never took.
        if (result.IsError == true)
        {
            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            throw new InvalidOperationException(
                $"{ChannelProtocol.RegisterAgentsTool} was refused by {ChannelId}: {text}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopPumpAsync();
        _messageChannel.Writer.TryComplete();
        var client = _client;
        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null)
        {
            return false;
        }

        try
        {
            await client.ListToolsAsync(cancellationToken: ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's own shutdown, not ill health. Answering false here makes the run call
            // this a failed check and reconnect on a token that is already cancelled, which returns
            // without connecting anything — and everything after it then works against a dead
            // client believing the link is up. Everything else, including a transport cancellation
            // the caller never asked for, is what "not connected" means (ADR 0011).
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task ReconnectAsync(string endpoint, CancellationToken ct)
    {
        await StopPumpAsync();
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        await ConnectAsync(endpoint, ct);
    }

    // Two targets resolving at once can both find the set unfetched and each ask; that costs one
    // extra round trip on the first turn of a generation and settles on the same answer, which is
    // cheaper than serialising every probe behind a lock. The client comes in as the caller's own
    // snapshot and is what a cached answer is matched against, because a probe can outlive its
    // generation: a reconnect mid-flight swaps the client, and a probe that asked the old server
    // must not pin the new connection to a tool set its server may not have.
    private async Task<bool> OffersToolAsync(McpClient client, string toolName, CancellationToken ct)
    {
        var tools = _tools;
        if (tools is null || !ReferenceEquals(tools.Owner, client))
        {
            tools = new ToolSet(
                client,
                (await client.ListToolsAsync(cancellationToken: ct))
                    .Select(tool => tool.Name)
                    .ToHashSet(StringComparer.Ordinal));
            _tools = tools;
        }

        return tools.Names.Contains(toolName);
    }

    private sealed record ToolSet(McpClient Owner, IReadOnlySet<string> Names);

    // The client every operation works against, read once. Re-reading the field after a guard —
    // or behind a null-forgiving operator — races ReconnectAsync nulling it and turns the five
    // documented not-connected behaviours (ADR 0011) into a NullReferenceException.
    private McpClient RequireClient() =>
        _client ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
}