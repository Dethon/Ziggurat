using System.Collections.Concurrent;

namespace Accounts.Admin;

// A household's admin changes, one at a time. A change reads the section it is about, lays itself
// over it and saves the section whole — and a key's save asks its provider between the read and the
// save — so two owners' changes in flight at once each saved over what the other had not written
// yet: Bruno's Idealista keys, saved while Ana's OpenRouter key was being probed, were gone once
// hers was saved. Each change of a household now waits for the one before it and reads what that one
// saved. A filter on the owners' group, so a route added later is covered without saying so; reads
// are not held. One accounts service holds the store (SQLite in its volume), so the queue is its own.
public sealed class OneChangeAtATime
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _households = new(StringComparer.Ordinal);

    public static void Apply(RouteGroupBuilder household) =>
        household.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
                || AdminCaller.Of(http) is not { } caller)
            {
                return await next(context);
            }

            return await http.RequestServices.GetRequiredService<OneChangeAtATime>()
                .InTurnAsync(caller.HouseholdId, () => next(context).AsTask(), http.RequestAborted);
        });

    // A change of the household named, made once the one before it is done. An operator's route that
    // saves a section an owner's change also saves (the apps feature) takes the turn of the household
    // its path names, not of its caller's.
    public async Task<T> InTurnAsync<T>(string householdId, Func<Task<T>> change, CancellationToken ct)
    {
        var turn = _households.GetOrAdd(householdId, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync(ct);
        try
        {
            return await change();
        }
        finally
        {
            turn.Release();
        }
    }
}