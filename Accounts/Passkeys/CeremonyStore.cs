using System.Collections.Concurrent;
using Accounts.Store;

namespace Accounts.Passkeys;

// The options a ceremony began with, held between its two requests: the finishing request has to be
// checked against the challenge the server issued, never one the browser reports. Kept in memory,
// because a ceremony lasts a minute and a restart in the middle of one only means pressing the
// button again.
public sealed class CeremonyStore(AccountsClock clock)
{
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (object Ceremony, DateTimeOffset Until)> _held = new();

    public string Hold(object ceremony)
    {
        var now = clock.Time.GetUtcNow();
        foreach (var stale in _held.Where(h => h.Value.Until <= now).Select(h => h.Key).ToList())
        {
            _held.TryRemove(stale, out _);
        }

        var id = Tokens.NewSecret();
        _held[id] = (ceremony, now + _lifetime);
        return id;
    }

    // Once: a finishing request replayed finds nothing to finish.
    public T? Take<T>(string id) where T : class =>
        _held.TryRemove(id, out var held) && held.Until > clock.Time.GetUtcNow() ? held.Ceremony as T : null;
}