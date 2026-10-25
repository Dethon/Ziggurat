using System.Collections.Concurrent;
using System.Security.Cryptography;
using McpServerWebSearch.Settings;

namespace McpServerWebSearch.Handoff;

// A person's logins page, reached by a link minted for them alone: the handoff's link rules — one
// claim, ten minutes, bound to the person — and a claimed page answers its own browser for as
// long again. The person is fixed when the link is minted, so nothing the page sends can name
// somebody else.
public sealed class LoginsPages(OneTimeLinks links, HandoffConfiguration settings, TimeProvider time)
{
    public const string TicketKind = "logins";

    private readonly ConcurrentDictionary<string, (string Person, DateTimeOffset Until)> _minted = new();
    private readonly ConcurrentDictionary<string, (string Person, DateTimeOffset Until)> _views = new();

    private TimeSpan Lifetime => TimeSpan.FromMinutes(settings.TicketMinutes);

    public string LinkFor(string token) => $"{settings.PublicBaseUrl.TrimEnd('/')}/browse/logins/{token}";

    public async Task<string> MintAsync(string person)
    {
        var token = await links.MintAsync(new OneTimeLinks.Ticket(TicketKind, person), Lifetime);
        _minted[token] = (person, time.GetUtcNow() + Lifetime);
        return token;
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

        var viewKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _views[viewKey] = (minted.Person, time.GetUtcNow() + Lifetime);
        return viewKey;
    }

    public string? PersonOf(string? viewKey) =>
        viewKey is not null && _views.TryGetValue(viewKey, out var view) && view.Until > time.GetUtcNow()
            ? view.Person
            : null;
}