using System.Collections.Concurrent;
using System.Security.Cryptography;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerWebSearch.Settings;
using Microsoft.Extensions.Logging;

namespace McpServerWebSearch.Handoff;

// The live handoffs of this process. A handoff is a handoff tab in the person's context, the
// conversation that asked for it, and the one-time link that opens its view. The link has to be
// claimed within its lifetime; once claimed the view lives for its own, and either running out
// closes the tab and leaves nothing behind. All of it is in memory, beside the tab it names: a
// restart closes the tab, and a ticket that outlived one refuses rather than opening a blank view.
public sealed class LoginHandoffs(
    IHandoffBrowser browser,
    OneTimeLinks links,
    ChannelNotificationEmitter wakes,
    HandoffConfiguration settings,
    TimeProvider time,
    ILogger<LoginHandoffs> logger)
{
    public const string TicketKind = "handoff";

    private readonly ConcurrentDictionary<string, LiveHandoff> _live = new();

    public sealed class LiveHandoff(string id, string person, ConversationContext context, string url, string token)
    {
        public string Id { get; } = id;
        public string Person { get; } = person;

        // The whole context of the turn that asked: the wake goes back to its conversation and
        // reply target, the satellite address included.
        public ConversationContext Context { get; } = context;
        public string Url { get; } = url;
        public string Token { get; } = token;
        internal string? ViewKey { get; set; }
        internal ITimer? Expiry { get; set; }
    }

    public string LinkFor(string token) => $"{settings.PublicBaseUrl.TrimEnd('/')}/browse/handoff/{token}";

    public async Task<LiveHandoff> BeginAsync(
        string person, ConversationContext context, string url, CancellationToken ct)
    {
        var id = await browser.OpenHandoffAsync(person, url, ct);
        var token = await links.MintAsync(new OneTimeLinks.Ticket(TicketKind, id), TicketLifetime);
        var handoff = new LiveHandoff(id, person, context, url, token);
        _live[id] = handoff;
        handoff.Expiry = time.CreateTimer(_ => _ = ExpireAsync(id), null, TicketLifetime, Timeout.InfiniteTimeSpan);
        return handoff;
    }

    public Task<bool> IsLinkLiveAsync(string token) => links.IsLiveAsync(token);

    // Spends the link and binds the view to a key only the claiming browser holds. Null when the
    // link was spent, ran out, or names a tab this process no longer has.
    public async Task<(LiveHandoff Handoff, string ViewKey)?> ClaimAsync(string token)
    {
        if (await links.ClaimAsync(token, TicketKind) is not { } ticket
            || !_live.TryGetValue(ticket.Id, out var handoff)
            || !browser.IsHandoffOpen(ticket.Id))
        {
            return null;
        }

        var viewKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        handoff.ViewKey = viewKey;
        handoff.Expiry?.Change(ViewLifetime, Timeout.InfiniteTimeSpan);
        return (handoff, viewKey);
    }

    public LiveHandoff? ByViewKey(string? viewKey) =>
        viewKey is null ? null : _live.Values.FirstOrDefault(h => h.ViewKey is { } key && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(key), System.Text.Encoding.ASCII.GetBytes(viewKey)));

    public bool IsOpen(LiveHandoff handoff) => _live.ContainsKey(handoff.Id) && browser.IsHandoffOpen(handoff.Id);

    // The person pressed Done: the login now lives in their context, the tab is closed, and the
    // agent is woken where it was asked.
    public async Task<bool> CompleteAsync(LiveHandoff handoff)
    {
        if (!_live.TryRemove(handoff.Id, out _))
        {
            return false;
        }

        handoff.Expiry?.Dispose();
        await browser.CloseHandoffAsync(handoff.Id);
        await wakes.EmitAsync(LoginWake.Compose(handoff, time.GetUtcNow()));
        return true;
    }

    // Abandoned or out of time: the tab goes, the link goes, and nothing else happens.
    private async Task ExpireAsync(string id)
    {
        if (!_live.TryRemove(id, out var handoff))
        {
            return;
        }

        handoff.Expiry?.Dispose();
        try
        {
            await links.RevokeAsync(handoff.Token);
            await browser.CloseHandoffAsync(id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Closing an expired login handoff failed");
        }
    }

    private TimeSpan TicketLifetime => TimeSpan.FromMinutes(settings.TicketMinutes);

    private TimeSpan ViewLifetime => TimeSpan.FromMinutes(settings.ViewMinutes);
}