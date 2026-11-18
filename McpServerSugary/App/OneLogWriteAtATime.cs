using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.App;

// A person's log changes, one at a time. A change is checked against the log as it is — no day in two
// periods, one open pen of a kind, one exercise running, a day's one note — and then saved, so two of
// her phones changing it at once were each checked against a log that had neither change: two periods
// started the same day, and a day's note written twice was refused by its key with the words lost.
// Each change of hers now waits for the one before it and is checked against what that one saved.
// Only her log's own routes: an estimate or a label waits on a model, and holding her other changes
// behind it would be the app freezing for no reason. The pantry is the household's, so its changes
// wait for each other across the household: a save is laid over the food or recipe as it is now
// (StaleEdits), and two people saving one recipe at the same instant each read it before the other
// wrote — one change was written away, or the save failed outright — and a food deleted while a
// recipe that uses it was saved slipped past the check that keeps it. A route that waits on somebody
// else before it writes (linking her LibreLinkUp account logs in first) says so (TakesItsOwnTurn) and
// takes her turn around its write alone (TurnAsync), so her meals are not held behind a slow login.
public sealed class OneLogWriteAtATime
{
    private static readonly string[] _log =
        ["/meals", "/doses", "/pens", "/exercise", "/periods", "/day-notes", "/glucose"];
    private static readonly string[] _pantry = ["/foods", "/recipes"];

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _people = new(StringComparer.Ordinal);

    public static void Apply(RouteGroupBuilder api) =>
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
                || http.GetEndpoint()?.Metadata.GetMetadata<TakesItsOwnTurn>() is not null
                || Person.Of(http) is not { } person
                || KeyOf(http.Request.Path, person.Account) is not { } key)
            {
                return await next(context);
            }

            using (await http.RequestServices.GetRequiredService<OneLogWriteAtATime>().TakeAsync(key, http.RequestAborted))
            {
                return await next(context);
            }
        });

    // Her turn, for a route that took its own: held until disposed.
    public Task<IDisposable> TurnAsync(Account person, CancellationToken ct) => TakeAsync(PersonKey(person), ct);

    private async Task<IDisposable> TakeAsync(string key, CancellationToken ct)
    {
        var turn = _people.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync(ct);
        return new Held(turn);
    }

    private static string PersonKey(Account account) => $"{account.TenantId}:{account.UserId}";

    private static string? KeyOf(PathString path, Account account) =>
        _log.Any(group => path.StartsWithSegments("/api" + group)) ? PersonKey(account)
        : _pantry.Any(group => path.StartsWithSegments("/api" + group)) ? $"{account.TenantId}:pantry"
        : null;

    private sealed class Held(SemaphoreSlim turn) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                turn.Release();
            }
        }
    }
}

// On a route of her log that takes her turn itself, around its write alone (OneLogWriteAtATime.TurnAsync).
public sealed class TakesItsOwnTurn;