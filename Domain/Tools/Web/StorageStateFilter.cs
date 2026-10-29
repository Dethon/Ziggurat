using System.Text;
using System.Text.Json.Nodes;

namespace Domain.Tools.Web;

// A Playwright storage state (cookies, and per origin its localStorage and IndexedDB) cut down to
// what may be kept. A save is an allowlist (Only): the domains of the person's remembered logins
// and nothing else, so a cookie nobody chose to remember — an unticked login's, a third party's an
// iframe set — never reaches the disk whichever host it came from. A forgotten login's domains
// leave a live context the other way round (Without), since everything else in it stays live.
// A cookie or an origin belongs to a domain when its host's registrable domain is that domain.
public static class StorageStateFilter
{
    public enum Dropped
    {
        Nothing,
        IndexedDb,
        IndexedDbAndLocalStorage
    }

    public sealed record CappedState(string State, Dropped Dropped);

    public static string Only(string storageState, IReadOnlyCollection<string> domains)
    {
        HashSet<string> kept = [.. domains.Select(RegistrableDomain.Of)];
        return Filter(storageState, host => host is { Length: > 0 } && kept.Contains(RegistrableDomain.Of(host)));
    }

    public static string Without(string storageState, IReadOnlyCollection<string> domains)
    {
        HashSet<string> excluded = [.. domains.Select(RegistrableDomain.Of)];
        return Filter(storageState, host => !(host is { Length: > 0 } && excluded.Contains(RegistrableDomain.Of(host))));
    }

    // The cookies of one state over the origins of another: a save after use exports cookies
    // alone, and what the last full export stored of each origin's storage stands.
    public static string WithCookiesOf(string storageState, string cookiesState)
    {
        var state = Parse(storageState);
        state["cookies"] = Parse(cookiesState)["cookies"]?.DeepClone() ?? new JsonArray();
        return state.ToJsonString();
    }

    // Kept under a size a record may reach: every origin's IndexedDB goes first, since that is
    // what grows to tens of megabytes on a mail client; then localStorage. The cookies — the
    // login itself — are never dropped, so an oversized state still saves the session.
    public static CappedState Capped(string storageState, long maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(storageState) <= maxBytes)
        {
            return new CappedState(storageState, Dropped.Nothing);
        }

        var state = Parse(storageState);
        foreach (var origin in Origins(state))
        {
            origin.Remove("indexedDB");
        }

        var withoutIndexedDb = state.ToJsonString();
        if (Encoding.UTF8.GetByteCount(withoutIndexedDb) <= maxBytes)
        {
            return new CappedState(withoutIndexedDb, Dropped.IndexedDb);
        }

        state["origins"] = new JsonArray();
        return new CappedState(state.ToJsonString(), Dropped.IndexedDbAndLocalStorage);
    }

    private static string Filter(string storageState, Func<string?, bool> keepHost)
    {
        var state = Parse(storageState);
        Keep(state, "cookies", cookie => keepHost(cookie["domain"]?.GetValue<string>()));
        Keep(state, "origins", origin =>
            keepHost(Uri.TryCreate(origin["origin"]?.GetValue<string>(), UriKind.Absolute, out var uri) ? uri.Host : null));
        return state.ToJsonString();
    }

    private static JsonObject Parse(string storageState) => JsonNode.Parse(storageState)?.AsObject() ?? new JsonObject();

    private static IEnumerable<JsonObject> Origins(JsonObject state) =>
        state["origins"] is JsonArray origins ? origins.OfType<JsonObject>() : [];

    private static void Keep(JsonObject state, string property, Func<JsonObject, bool> keep)
    {
        if (state[property] is not JsonArray entries)
        {
            return;
        }

        state[property] = new JsonArray([.. entries.OfType<JsonObject>().Where(keep).Select(e => e.DeepClone())]);
    }
}