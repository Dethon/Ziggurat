using System.Collections.Concurrent;
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
// behind it would be the app freezing for no reason; the pantry is the household's and keyed by id.
public sealed class OneLogWriteAtATime
{
    private static readonly string[] _log = ["/meals", "/doses", "/pens", "/exercise", "/periods", "/day-notes"];

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _people = new(StringComparer.Ordinal);

    public static void Apply(RouteGroupBuilder api) =>
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
                || !_log.Any(group => http.Request.Path.StartsWithSegments("/api" + group))
                || Person.Of(http) is not { } person)
            {
                return await next(context);
            }

            var turn = http.RequestServices.GetRequiredService<OneLogWriteAtATime>()
                ._people.GetOrAdd($"{person.Account.TenantId}:{person.Account.UserId}", _ => new SemaphoreSlim(1, 1));
            await turn.WaitAsync(http.RequestAborted);
            try
            {
                return await next(context);
            }
            finally
            {
                turn.Release();
            }
        });
}