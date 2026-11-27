using JetBrains.Annotations;

namespace Domain.Apps;

// A person's apps for WebChat's space switcher, spelled once for its two ends: the apps server answers it
// on its MCP listener (the deployment's network alone: the proxy sends nothing there), and the chat hub
// asks it with an assertion the accounts service signed for the connection's session on
// AppChannels.PersonsApps, which it signs for the hub alone. The household and the person are that
// assertion's: the request names the household's app spaces' apps (`shared`, the ids the hub read in the
// household's spaces section) and nobody.
public static class PersonsApps
{
    public const string Path = "/internal/persons-apps";
    public const string SharedParameter = "shared";

    public static string PathFor(IEnumerable<string> shared) =>
        shared.Aggregate(Path, (path, id) =>
            $"{path}{(path == Path ? '?' : '&')}{SharedParameter}={Uri.EscapeDataString(id)}");
}

// One of a person's apps: their own published app (Own), or one the household shares that they asked the
// link of — named as its served commit names it.
[PublicAPI]
public sealed record PersonsApp(string Id, string Name, string AccentColor, string Url, bool Own);

[PublicAPI]
public sealed record PersonsAppList(IReadOnlyList<PersonsApp> Apps);