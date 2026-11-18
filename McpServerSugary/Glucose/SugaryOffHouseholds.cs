using Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// Which households turn Sugary off, as the glucose poller and the watch engine ask it. Neither wakes for a
// request, so neither holds a proof of a household: they read the accounts service's view across
// households (`sugary-off`) on Sugary's own credential, which the reader keeps a minute.
//
// When it cannot be read just now every household is taken as on — a medical alarm must not go silent
// because the accounts service blinked — and said once per outage. A failure is remembered for a while,
// so a service that hangs or refuses is not asked again on every five-second sweep, each poll waiting out
// its timeout.
public sealed class SugaryOffHouseholds(
    IHouseholdConfiguration configuration, TimeProvider clock, ILogger<SugaryOffHouseholds> logger)
{
    public static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(1);

    private static readonly IReadOnlySet<string> _none = new HashSet<string>();

    private readonly Lock _gate = new();
    private DateTimeOffset _askAgainAt = DateTimeOffset.MinValue;
    private bool _failing;

    public async Task<IReadOnlySet<string>> OffAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (clock.GetUtcNow() < _askAgainAt)
            {
                return _none;
            }
        }

        try
        {
            var off = await configuration.HouseholdsWithSugaryOffAsync(ct);
            if (Recovered())
            {
                logger.LogInformation("The accounts service says again which households turn Sugary off");
            }

            return off.ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (Failed())
            {
                logger.LogWarning(ex,
                    "Could not learn which households turn Sugary off; every household's glucose is polled and judged until it can be");
            }

            return _none;
        }
    }

    public async Task<bool> IsOffAsync(string household, CancellationToken ct) => (await OffAsync(ct)).Contains(household);

    private bool Recovered()
    {
        lock (_gate)
        {
            var was = _failing;
            _failing = false;
            return was;
        }
    }

    // True at an outage's first failure.
    private bool Failed()
    {
        lock (_gate)
        {
            _askAgainAt = clock.GetUtcNow() + AfterFailure;
            var first = !_failing;
            _failing = true;
            return first;
        }
    }
}