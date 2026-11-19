using Domain.Contracts;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// Which households turn Sugary off, and whom an owner has not turned it on for, as the work that wakes
// for no request asks it: a Sugary-off household's watches judge and fire nothing (the feature takes
// Sugary from its agents, never the app, so its links are still polled); a person Sugary is off for has
// no log at all, so their link is not polled, their watches judge nothing and their pens remind nobody.
// None of it holds proof of a household: it reads the accounts service's view across households
// (`sugary-off`) on Sugary's own credential, which the reader keeps a minute.
//
// When it cannot be read just now every household and everyone is taken as on — a medical alarm must not go silent
// because the accounts service blinked — and said once per outage. A failure is remembered for a while,
// so a service that hangs or refuses is not asked again on every five-second sweep, each poll waiting out
// its timeout.
public sealed class SugaryOff(
    IHouseholdConfiguration configuration, TimeProvider clock, ILogger<SugaryOff> logger)
{
    public static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(1);

    private static readonly Now _none = new(new HashSet<string>(), new HashSet<Account>());

    // One answer: the households turning Sugary off and the people it is off for.
    public sealed record Now(IReadOnlySet<string> Households, IReadOnlySet<Account> People)
    {
        public bool HouseholdOff(string household) => Households.Contains(household);

        // No log for them: an owner has not turned Sugary on for them.
        public bool PersonOff(Account person) => People.Contains(person);
    }

    private readonly Lock _gate = new();
    private DateTimeOffset _askAgainAt = DateTimeOffset.MinValue;
    private bool _failing;

    public async Task<Now> OffAsync(CancellationToken ct)
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
            var off = await configuration.SugaryOffAsync(ct);
            if (Recovered())
            {
                logger.LogInformation("The accounts service says again whom Sugary is off for");
            }

            return new Now(off.Households.ToHashSet(StringComparer.Ordinal), (off.People ?? []).ToHashSet());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (Failed())
            {
                logger.LogWarning(ex,
                    "Could not learn whom Sugary is off for; everyone's glucose is polled and watched and pens remind until it can be");
            }

            return _none;
        }
    }

    // Whether their glucose watches are judged: not in a household turning Sugary off, nor for a person
    // it is off for.
    public async Task<bool> IsOffAsync(Account person, CancellationToken ct) =>
        await OffAsync(ct) is var off && (off.HouseholdOff(person.TenantId) || off.PersonOff(person));

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