using System.Collections.Concurrent;
using Domain.Contracts;
using McpServerWebSearch.Settings;

namespace McpServerWebSearch.Handoff;

// A person's logins page, reached by a link minted for them alone: the handoff's link rules — one
// claim, ten minutes, bound to the person — and a claimed page answers its own browser for as
// long again. The person is fixed when the link is minted, so nothing the page sends can name
// somebody else.
public sealed class LoginsPages(OneTimeLinks links, HandoffConfiguration settings, TimeProvider time) : ILoginsPages
{
    public const string TicketKind = "logins";

    // A link or a claimed page: whose it is, and until when.
    private sealed record Grant(string Person, DateTimeOffset Until);

    private readonly ConcurrentDictionary<string, Grant> _minted = new();
    private readonly ConcurrentDictionary<string, Grant> _views = new();

    private TimeSpan Lifetime => TimeSpan.FromMinutes(settings.TicketMinutes);

    public int LinkMinutes => settings.TicketMinutes;

    public async Task<string> MintAsync(string person, CancellationToken ct)
    {
        Prune();
        var token = await links.MintAsync(new OneTimeLinks.Ticket(TicketKind, person), Lifetime);
        _minted[token] = new Grant(person, time.GetUtcNow() + Lifetime);
        return BrowseLinks.For(settings, "logins", token);
    }

    // Links nobody claimed and pages left open are dropped once they run out, so neither table
    // grows with every link ever minted.
    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var (key, _) in _minted.Where(entry => entry.Value.Until <= now))
        {
            _minted.TryRemove(key, out _);
        }

        foreach (var (key, _) in _views.Where(entry => entry.Value.Until <= now))
        {
            _views.TryRemove(key, out _);
        }
    }

    public async Task<bool> IsLinkLiveAsync(string token) =>
        _minted.TryGetValue(token, out var minted) && minted.Until > time.GetUtcNow()
                                                   && await links.IsLiveAsync(token);

    public async Task<string?> ClaimAsync(string token)
    {
        // Both spent at once, whatever either answers: a refused claim leaves nothing to retry.
        var mintedHere = _minted.TryRemove(token, out var minted);
        var ticket = await links.ClaimAsync(token, TicketKind);
        if (!mintedHere || ticket is null || ticket.Id != minted.Person || minted.Until <= time.GetUtcNow())
        {
            return null;
        }

        var viewKey = BrowseLinks.NewViewKey();
        _views[viewKey] = new Grant(minted!.Person, time.GetUtcNow() + Lifetime);
        return viewKey;
    }

    public string? PersonOf(string? viewKey) =>
        viewKey is not null && _views.TryGetValue(viewKey, out var view) && view.Until > time.GetUtcNow()
            ? view.Person
            : null;
}