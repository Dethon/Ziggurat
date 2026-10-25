using System.Text.Json.Nodes;

namespace Domain.Tools.Web;

// A Playwright storage state (cookies, and per origin its localStorage and IndexedDB) without the
// sites named: a live-only login left out of every save, a forgotten one left out of the context
// rebuilt without it. A cookie or an origin belongs to a site when its host's registrable domain
// is that site.
public static class StorageStateFilter
{
    public static string Without(string storageState, IReadOnlyCollection<string> sites)
    {
        var state = JsonNode.Parse(storageState)?.AsObject() ?? new JsonObject();
        if (sites.Count == 0)
        {
            return state.ToJsonString();
        }

        HashSet<string> excluded = [.. sites.Select(RegistrableDomain.Of)];
        bool Excluded(string? host) => host is { Length: > 0 } && excluded.Contains(RegistrableDomain.Of(host));

        Keep(state, "cookies", cookie => !Excluded(cookie["domain"]?.GetValue<string>()));
        Keep(state, "origins", origin =>
            !(Uri.TryCreate(origin["origin"]?.GetValue<string>(), UriKind.Absolute, out var uri) && Excluded(uri.Host)));
        return state.ToJsonString();
    }

    private static void Keep(JsonObject state, string property, Func<JsonObject, bool> keep)
    {
        if (state[property] is not JsonArray entries)
        {
            return;
        }

        state[property] = new JsonArray([.. entries.OfType<JsonObject>().Where(keep).Select(e => e.DeepClone())]);
    }
}