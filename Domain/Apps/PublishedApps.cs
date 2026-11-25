using JetBrains.Annotations;

namespace Domain.Apps;

// The apps server's listing of a household's published apps, spelled once for its two ends: the apps
// server answers it on its MCP listener (the deployment's network alone: the proxy sends nothing there),
// and the accounts service's admin API asks it, for the spaces page an owner shares an app from. The
// asker presents an assertion it signed itself for the owner its admin gate verified, on
// AppChannels.PublishedApps, and the household is that assertion's: the request names none, and no
// secret exists for this.
public static class PublishedApps
{
    public const string Path = "/internal/published-apps";
}

// What an owner sees of an app before sharing it with the household: who made it, what it reads of the
// household and which outside origins it talks to — the served commit's manifest, never the working tree.
[PublicAPI]
public sealed record PublishedApp(
    string Id,
    string Slug,
    string Name,
    string AccentColor,
    string Description,
    string AuthorId,
    IReadOnlyList<string> Reads,
    IReadOnlyList<string> Connect,
    string Url);

[PublicAPI]
public sealed record PublishedAppList(IReadOnlyList<PublishedApp> Apps);