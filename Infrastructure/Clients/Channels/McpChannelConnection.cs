using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
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
    TimeSpan? healthCheckInterval = null,
    // The channel server's own credential, which its /mcp asks for, found by the host its endpoint
    // is dialled at. Presented on the first dial and on every reconnect, since both go through
    // ConnectAsync. Null presents nothing, which a gated
    // server refuses — the dial then fails and is retried like any other.
    string? mcpSecret = null,
    // The agent's call tokens. Every call this connection makes is a tool call the channel server's
    // gate asks one for: an approval is signed for the turn it guards, and the rest — the poll, a
    // reply, the catalog — for nobody, since the agent makes them for no person. Null signs nothing,
    // and a gated channel server refuses every call.
    CallTokens? callTokens = null)
    : IChannelConnection, IAlertChannel, IMcpChannelConnection, IAsyncDisposable
{
    private static readonly TimeSpan _defaultHealthCheckInterval = TimeSpan.FromSeconds(30);

    // A retry that has backed off this far is waiting on something that will not be fixed by
    // waiting longer, and the next attempt should not be minutes away.
    private static readonly TimeSpan _maxReconnectDelay = TimeSpan.FromSeconds(30);

    // How long a dial may spend on the initialization handshake before it counts as a failure. The
    // SDK's own default is a minute, which is longer than the health-check cadence above and long
    // enough that a channel server that is simply down holds the dial open past the point where
    // retrying it would have been the useful thing to do. The handshake is one round trip against a
    // server that has already accepted the connection, so a server answering it needs a fraction of
    // this; anything slower is the retry's business, not the dial's.
    private static readonly TimeSpan _initializationTimeout = TimeSpan.FromSeconds(10);

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
    private readonly Channel<AlertSignal> _alertChannel = Channel.CreateUnbounded<AlertSignal>();
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

    // A household's own server's connection serves that household alone; a dialled one, every one.
    public ChannelHousehold Household { get; init; } = ChannelHousehold.Every;

    public bool HostsReviewCopies { get; init; }

    public bool IsConnected => _client is not null;

    public IAsyncEnumerable<ChannelMessage> Messages => _messageChannel.Reader.ReadAllAsync();

    public IAsyncEnumerable<AlertSignal> Alerts => _alertChannel.Reader.ReadAllAsync();

    // A URL dialled with this connection's own credential.
    public Task RunAsync(string endpoint, Func<IReadOnlyList<AgentCatalogEntry>> catalog, CancellationToken ct) =>
        RunAsync(McpServerEndpoint.Configured(endpoint, mcpSecret), catalog, ct);

    // Connect, register the catalog, watch health, reconnect, re-register — for as long as the
    // token lives. The order is here rather than in a caller because the order is about this.
    //
    // When the token goes the connection lets go: a run that ended is a channel not connected (ADR
    // 0011), whether the agent is stopping or the household server's tunnel this run was bound to has
    // closed, and the next run starts from nothing.
    public async Task RunAsync(
        McpServerEndpoint endpoint, Func<IReadOnlyList<AgentCatalogEntry>> catalog, CancellationToken ct)
    {
        var interval = healthCheckInterval ?? _defaultHealthCheckInterval;
        try
        {
            await WithRetryAsync(endpoint, reconnect: false, ct);
            // The catalog and the households' Lemonade models are read once per tick, and that one
            // reading is both what is sent and what the next tick is compared with: read again after a
            // registration, a change landing in between would pass for one already sent.
            var entries = Registration(catalog());
            var registered = await TryRegisterAgentsAsync(entries, ct);
            var lastRegistered = registered ? Fingerprint(entries) : null;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                entries = Registration(catalog());

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
        finally
        {
            await DisconnectAsync();
        }
    }

    // One loop for both, because a reconnect is a connect that had a predecessor: a fix to the
    // back-off could otherwise land in one and miss the other.
    private async Task WithRetryAsync(McpServerEndpoint endpoint, bool reconnect, CancellationToken ct)
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
                    verb, ChannelId, endpoint.Address, attempt);
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
    // Each household's own Lemonade box's models, sent with the catalog to a channel that shows people
    // a picker; null sends none. Read with the catalog, so a box gaining or losing a model re-registers.
    public Func<IReadOnlyDictionary<string, IReadOnlyList<PatchableModel>>>? HouseholdLemonadeModels { get; init; }

    private RegisterAgentsParams Registration(IReadOnlyList<AgentCatalogEntry> agents) =>
        new() { Agents = agents, HouseholdLemonadeModels = HouseholdLemonadeModels?.Invoke() };

    private static string Fingerprint(RegisterAgentsParams registration) =>
        JsonSerializer.Serialize(registration, ChannelProtocol.SerializerOptions);

    private async Task<bool> TryRegisterAgentsAsync(RegisterAgentsParams registration, CancellationToken ct)
    {
        try
        {
            await RegisterAsync(registration, ct);
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

    public Task ConnectAsync(string endpoint, CancellationToken ct) =>
        ConnectAsync(McpServerEndpoint.Configured(endpoint, mcpSecret), ct);

    // A dialled server presents the endpoint's credential; a household's server is reached through
    // its tunnel's client, which presents the tunnel's own, and is signed for the synthetic address it
    // was admitted with.
    public async Task ConnectAsync(McpServerEndpoint endpoint, CancellationToken ct)
    {
        // Never leave a second pump alive on this connection: two of them share one subscriberId
        // and displace each other's waiter, which the inbox retires with an instant empty batch.
        await StopPumpAsync();

        var client = await McpClient.CreateAsync(
            McpClientManager.TransportFor(endpoint, callTokens),
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = $"{ChannelProtocol.ChannelClientNamePrefix}{ChannelId}",
                    Version = "1.0.0"
                },
                InitializationTimeout = _initializationTimeout
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
        switch (item.Kind)
        {
            case ChannelInboxItemKind.Message:
                HandleChannelMessageNotification(
                    JsonSerializer.SerializeToElement(item.Message, ChannelProtocol.SerializerOptions));
                break;
            case ChannelInboxItemKind.Cancel:
                HandleChannelCancelNotification(
                    JsonSerializer.SerializeToElement(item.Cancel, ChannelProtocol.SerializerOptions));
                break;
            // An alert is no turn: it goes to the relay that rings it, never to a conversation.
            case ChannelInboxItemKind.Alert when item.Alert is { } alert:
                _alertChannel.Writer.TryWrite(new AlertSignal.Ring(alert));
                break;
            case ChannelInboxItemKind.AlertStopped when item.AlertStopped is { } stopped:
                _alertChannel.Writer.TryWrite(new AlertSignal.Stop(stopped.AlertId));
                break;
            default:
                logger?.LogWarning("Discarding a {Kind} inbox item on {ChannelId} with nothing in it", item.Kind, ChannelId);
                break;
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
            UserId = notification.UserId,
            Principal = notification.Principal,
            Assertion = notification.Assertion,
            Delegation = notification.Delegation,
            FireReport = notification.FireReport,
            HouseholdId = notification.HouseholdId,
            ReplyTo = notification.ReplyTo,
            Origin = notification.Origin,
            Location = notification.Location,
            SatelliteId = notification.SatelliteId,
            DismissedAlert = notification.DismissedAlert,
            ConfigPatch = notification.ConfigPatch,
            ResponseFormat = notification.ResponseFormat,
            ReviewCopy = notification.ReviewCopy,
            TurnKey = notification.TurnKey,
            Attachments = notification.Attachments
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

    public async Task<ToolApprovalResult?> AskApprovalAsync(ApprovalAsk ask, CancellationToken ct)
    {
        var client = RequireClient();
        // The turn's conversation context rides this hop as `_meta`, as it does on a tool the
        // model calls: an approval is asked from inside the tool invocation it guards, and the
        // channel that reads the answer has to see what the turn asked for — whose question it is,
        // and whether it was addressed to the local box, which is never put to a hosted judge.
        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = ChannelProtocol.RequestApprovalTool,
            Arguments = Arguments(new RequestApprovalParams
            {
                ConversationId = ask.ConversationId,
                Mode = ApprovalMode.Request,
                Requests = ask.Requests,
                ApprovalId = ask.ApprovalId
            }),
            Meta = ConversationContextMeta.Build(ask.Turn)
        }, ct);

        return ChannelProtocol.ParseApprovalAnswer(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
    }

    // Null on every way of not being answered here: not connected, a channel that offers nothing
    // (a dual-role server, voice), a refusal of the call. None of them is the person saying no.
    public async Task<ToolApprovalResult?> OfferApprovalAsync(ApprovalAsk ask, CancellationToken ct)
    {
        var client = _client;
        if (client is null || ask.Asker is not { } asker
            || !await OffersToolAsync(client, ChannelProtocol.OfferApprovalTool, ct))
        {
            return null;
        }

        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = ChannelProtocol.OfferApprovalTool,
            Arguments = Arguments(new OfferApprovalParams
            {
                ApprovalId = ask.ApprovalId,
                ConversationId = ask.ConversationId,
                Asker = asker,
                AgentId = ask.Turn?.AgentId,
                Requests = ask.Requests
            }),
            Meta = ConversationContextMeta.Build(ask.Turn)
        }, ct);

        return result.IsError == true
            ? null
            : ChannelProtocol.ParseApprovalAnswer(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
    }

    public async Task SettleApprovalAsync(string approvalId, ToolApprovalResult result, CancellationToken ct)
    {
        var client = _client;
        if (client is null || !await OffersToolAsync(client, ChannelProtocol.SettleApprovalTool, ct))
        {
            return;
        }

        await client.CallToolAsync(
            ChannelProtocol.SettleApprovalTool,
            ChannelProtocol.ToArguments(new SettleApprovalParams { ApprovalId = approvalId, Result = result }),
            cancellationToken: ct);
    }

    // Signed for nobody, as the rest of the channel plumbing is: an alert is no turn, and the person
    // it is for is named in the offer, as an approval's asker is.
    public async Task<bool> OfferAlertAsync(ChannelAlertNotification alert, CancellationToken ct)
    {
        var client = _client;
        if (client is null || !await OffersToolAsync(client, ChannelProtocol.OfferAlertTool, ct))
        {
            return false;
        }

        var result = await client.CallToolAsync(
            ChannelProtocol.OfferAlertTool,
            ChannelProtocol.ToArguments(new OfferAlertParams
            {
                AlertId = alert.AlertId,
                Owner = alert.Owner,
                Text = alert.Text,
                Kind = alert.Kind,
                AgentId = alert.AgentId
            }),
            cancellationToken: ct);

        return result.IsError != true
               && result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text == ChannelProtocol.AlertDismissed;
    }

    public async Task SettleAlertAsync(string alertId, bool dismissed, CancellationToken ct)
    {
        var client = _client;
        if (client is null || !await OffersToolAsync(client, ChannelProtocol.SettleAlertTool, ct))
        {
            return;
        }

        await client.CallToolAsync(
            ChannelProtocol.SettleAlertTool,
            ChannelProtocol.ToArguments(new SettleAlertParams { AlertId = alertId, Dismissed = dismissed }),
            cancellationToken: ct);
    }

    private static Dictionary<string, JsonElement> Arguments<T>(T value) =>
        ChannelProtocol.ToArguments(value).ToDictionary(a => a.Key, a => (JsonElement)a.Value!);

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

    // Null on every way of not getting bytes — no client, no such tool, a refusal, a reconnect
    // under the call. Hydration turns all of them into the same placeholder, so a file that
    // cannot be fetched costs the turn its picture and nothing else.
    public async Task<byte[]?> FetchAttachmentAsync(string attachmentId, CancellationToken ct)
    {
        var client = _client;
        if (client is null)
        {
            return null;
        }

        try
        {
            if (!await OffersToolAsync(client, ChannelProtocol.FetchAttachmentTool, ct))
            {
                return null;
            }

            var result = await client.CallToolAsync(
                ChannelProtocol.FetchAttachmentTool,
                new Dictionary<string, object?> { ["attachmentId"] = attachmentId },
                cancellationToken: ct);

            if (result.IsError == true)
            {
                logger?.LogWarning(
                    "Channel {ChannelId} refused to hand over attachment {AttachmentId}",
                    ChannelId, attachmentId);
                return null;
            }

            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : Convert.FromBase64String(text);
        }
        catch (Exception ex) when (ex is McpException or ObjectDisposedException or FormatException)
        {
            logger?.LogWarning(ex,
                "Fetching attachment {AttachmentId} from channel {ChannelId} failed",
                attachmentId, ChannelId);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A reconnect disposed the client under the call. That is "not connected", which here
            // is a missing file rather than a failure the caller has to handle.
            return null;
        }
    }

    public async Task<bool> StoreAttachmentAsync(
        string conversationId, AttachmentReference attachment, byte[] bytes, CancellationToken ct)
    {
        var client = _client;
        if (client is null)
        {
            return false;
        }

        try
        {
            if (!await OffersToolAsync(client, ChannelProtocol.StoreAttachmentTool, ct))
            {
                return false;
            }

            var result = await client.CallToolAsync(
                ChannelProtocol.StoreAttachmentTool,
                new Dictionary<string, object?>
                {
                    ["conversationId"] = conversationId,
                    ["attachmentId"] = attachment.Id,
                    ["fileName"] = attachment.FileName,
                    ["mediaType"] = attachment.MediaType,
                    ["content"] = Convert.ToBase64String(bytes)
                },
                cancellationToken: ct);

            if (result.IsError == true)
            {
                logger?.LogWarning(
                    "Channel {ChannelId} refused to keep attachment {AttachmentId}: {Reason}",
                    ChannelId, attachment.Id, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is McpException or ObjectDisposedException)
        {
            logger?.LogWarning(ex,
                "Keeping attachment {AttachmentId} on channel {ChannelId} failed",
                attachment.Id, ChannelId);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A reconnect disposed the client under the call: not connected, so not kept.
            return false;
        }
    }

    public async Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        string? initialPrompt,
        string? address,
        string? existingConversationId,
        Principal? principal,
        bool group,
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
                    ["existingConversationId"] = existingConversationId,
                    ["principal"] = principal is null
                        ? null
                        : JsonSerializer.SerializeToElement(principal, ChannelProtocol.SerializerOptions),
                    ["group"] = group
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

    public Task RegisterAgentsAsync(IReadOnlyList<AgentCatalogEntry> agents, CancellationToken ct) =>
        RegisterAsync(Registration(agents), ct);

    private async Task RegisterAsync(RegisterAgentsParams registration, CancellationToken ct)
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
            ChannelProtocol.ToArguments(registration),
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

    public Task ReconnectAsync(string endpoint, CancellationToken ct) =>
        ReconnectAsync(McpServerEndpoint.Configured(endpoint, mcpSecret), ct);

    public async Task ReconnectAsync(McpServerEndpoint endpoint, CancellationToken ct)
    {
        await DisconnectAsync();
        await ConnectAsync(endpoint, ct);
    }

    private async Task DisconnectAsync()
    {
        await StopPumpAsync();
        if (_client is { } client)
        {
            _client = null;
            await client.DisposeAsync();
        }
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