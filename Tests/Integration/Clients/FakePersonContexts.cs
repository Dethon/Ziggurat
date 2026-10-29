using System.Text.Json.Nodes;
using Infrastructure.Clients.Browser;

namespace Tests.Integration.Clients;

// A person's browser context as cookies and origins in memory: a test sets what a site would have
// set, and reads back what a Forget left in the live context. Each export is counted, so a test can
// tell the save after use (cookies alone) from a full export.
internal sealed class FakePersonContexts : IPersonContexts
{
    private readonly Lock _lock = new();
    private JsonObject? _state;

    public event Action<string, string>? PersonActivity;

    public event Action? ContextsClosed;

    public int FullExports { get; private set; }

    public int CookieExports { get; private set; }

    public void RaiseActivity(string person, string url) => PersonActivity?.Invoke(person, url);

    public void RaiseContextsClosed()
    {
        lock (_lock)
        {
            _state = null;
        }

        ContextsClosed?.Invoke();
    }

    // Sets a cookie as a site would: one with the same name, domain and path is replaced.
    public void SetCookie(string name, string domain, string value)
    {
        lock (_lock)
        {
            var cookies = State()["cookies"]!.AsArray();
            var existing = cookies.OfType<JsonObject>().FirstOrDefault(c =>
                c["name"]!.GetValue<string>() == name && c["domain"]!.GetValue<string>() == domain);
            if (existing is not null)
            {
                cookies.Remove(existing);
            }

            cookies.Add(new JsonObject { ["name"] = name, ["value"] = value, ["domain"] = domain, ["path"] = "/" });
        }
    }

    public void SetOrigin(string origin, string localStorageValue, int indexedDbBytes = 0)
    {
        lock (_lock)
        {
            var origins = State()["origins"]!.AsArray();
            var existing = origins.OfType<JsonObject>().FirstOrDefault(o => o["origin"]!.GetValue<string>() == origin);
            if (existing is not null)
            {
                origins.Remove(existing);
            }

            var entry = new JsonObject
            {
                ["origin"] = origin,
                ["localStorage"] = new JsonArray(new JsonObject { ["name"] = "ls", ["value"] = localStorageValue })
            };
            if (indexedDbBytes > 0)
            {
                entry["indexedDB"] = new JsonArray(new JsonObject
                {
                    ["name"] = "db", ["version"] = 1, ["stores"] = new JsonArray(), ["blob"] = new string('i', indexedDbBytes)
                });
            }

            origins.Add(entry);
        }
    }

    // What the live context holds now, as Playwright would export it; null with no context.
    public string? Live
    {
        get
        {
            lock (_lock)
            {
                return _state?.ToJsonString();
            }
        }
    }

    public Task<string?> ExportStateAsync(string person, CancellationToken ct = default)
    {
        lock (_lock)
        {
            FullExports++;
            return Task.FromResult(_state?.ToJsonString());
        }
    }

    public Task<string?> ExportCookiesAsync(string person, CancellationToken ct = default)
    {
        lock (_lock)
        {
            CookieExports++;
            return Task.FromResult(_state is null
                ? null
                : new JsonObject { ["cookies"] = _state["cookies"]!.DeepClone(), ["origins"] = new JsonArray() }
                    .ToJsonString());
        }
    }

    public Task ReplaceStateAsync(string person, string storageState, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _state = JsonNode.Parse(storageState)!.AsObject();
        }

        return Task.CompletedTask;
    }

    private JsonObject State() =>
        _state ??= new JsonObject { ["cookies"] = new JsonArray(), ["origins"] = new JsonArray() };
}