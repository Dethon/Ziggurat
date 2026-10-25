using System.Collections.Concurrent;
using System.Security.Cryptography;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Browser;
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
    PersonLogins logins,
    HandoffConfiguration settings,
    TimeProvider time,
    ILogger<LoginHandoffs> logger) : ILoginHandoffs
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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.PublicBaseUrl);

    public int LinkMinutes => settings.TicketMinutes;

    public string LinkFor(string token) => BrowseLinks.For(settings, "handoff", token);

    public async Task<string> BeginAsync(
        string person, ConversationContext context, string url, string? inWebChatOf, CancellationToken ct)
    {
        // Counted before the tab opens: the login page's own requests must not arm a save of a
        // login nobody has finished.
        logins.BeginHandoff(person);
        string id;
        string token;
        try
        {
            id = await browser.OpenHandoffAsync(person, url, ct);
        }
        catch
        {
            await logins.AbandonHandoffAsync(person, [], CancellationToken.None);
            throw;
        }

        try
        {
            token = await links.MintAsync(new OneTimeLinks.Ticket(TicketKind, id), TicketLifetime);
        }
        catch
        {
            // No link, no handoff: the tab nobody can reach goes with it.
            await logins.AbandonHandoffAsync(person, browser.HandoffVisitedHosts(id), CancellationToken.None);
            await browser.CloseHandoffAsync(id);
            throw;
        }

        var handoff = new LiveHandoff(id, person, context, url, token);
        _live[id] = handoff;
        handoff.Expiry = time.CreateTimer(_ => _ = ExpireAsync(id), null, TicketLifetime, Timeout.InfiniteTimeSpan);

        var link = LinkFor(token);
        if (inWebChatOf is not null)
        {
            await wakes.EmitAsync(LoginWake.LinkInWebChat(handoff, link, inWebChatOf, LinkMinutes, time.GetUtcNow()));
        }

        return link;
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

        var viewKey = BrowseLinks.NewViewKey();
        handoff.ViewKey = viewKey;
        handoff.Expiry?.Change(ViewLifetime, Timeout.InfiniteTimeSpan);
        return (handoff, viewKey);
    }

    public LiveHandoff? ByViewKey(string? viewKey) =>
        viewKey is null ? null : _live.Values.FirstOrDefault(h => h.ViewKey is { } key && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(key), System.Text.Encoding.ASCII.GetBytes(viewKey)));

    public bool IsOpen(LiveHandoff handoff) => _live.ContainsKey(handoff.Id) && browser.IsHandoffOpen(handoff.Id);

    public bool CanRemember => logins.CanRemember;

    // The person pressed Done: the login now lives in their context — remembered, or only until
    // the browser restarts — the tab is closed, and the agent is woken where it was asked.
    public async Task<bool> CompleteAsync(LiveHandoff handoff, bool remember)
    {
        if (!_live.TryRemove(handoff.Id, out _))
        {
            return false;
        }

        handoff.Expiry?.Dispose();
        // The tab died under the view — the browser restarted — so there is no login to keep and
        // nothing to wake the agent about.
        if (!browser.IsHandoffOpen(handoff.Id))
        {
            await logins.AbandonHandoffAsync(handoff.Person, []);
            return false;
        }

        // Read before the close, which forgets the handoff and the hosts with it.
        var hosts = browser.HandoffVisitedHosts(handoff.Id);
        await browser.CloseHandoffAsync(handoff.Id);
        try
        {
            await logins.CompleteHandoffAsync(handoff.Person, hosts, remember);
        }
        catch (Exception ex)
        {
            // The login is in the person's context either way; only its surviving a restart is lost.
            logger.LogWarning(ex, "Saving the login {Person} handed off failed", handoff.Person);
        }

        await wakes.EmitAsync(LoginWake.Compose(handoff, time.GetUtcNow()));
        return true;
    }

    // Abandoned or out of time: the tab goes, the link goes, and nothing is emitted. Whatever the
    // person half did there stays in their context only until the browser restarts — no save
    // carries a login nobody finished.
    private async Task ExpireAsync(string id)
    {
        if (!_live.TryRemove(id, out var handoff))
        {
            return;
        }

        handoff.Expiry?.Dispose();
        try
        {
            await logins.AbandonHandoffAsync(handoff.Person, browser.HandoffVisitedHosts(id));
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