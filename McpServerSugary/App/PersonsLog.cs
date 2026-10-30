using McpServerSugary.App;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.Store;

// An app route's database is the log of the person the request acts for. A route asks for a
// SugaryDbContext and is bound this one — opened for the Person in the proxy's headers, its query
// filters keeping to their rows and their household's kitchen — rather than the container's, which
// is nobody's. Binding takes precedence over the container for a parameter, so a route cannot be
// handed the unowned context by accident; and the gate in front of /api has already answered a
// request naming nobody, so a route never sees that either. The context ends with the request.
public partial class SugaryDbContext
{
    public static ValueTask<SugaryDbContext?> BindAsync(HttpContext http)
    {
        if (Person.Of(http) is not { } person)
        {
            return ValueTask.FromResult<SugaryDbContext?>(null);
        }

        var db = http.RequestServices.GetRequiredService<SugaryStore>().For(person.Account);
        http.Response.RegisterForDisposeAsync(db);
        return ValueTask.FromResult<SugaryDbContext?>(db);
    }
}