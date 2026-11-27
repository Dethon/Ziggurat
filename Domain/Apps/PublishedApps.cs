using System.Text.Json.Serialization;
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
// household, which outside origins it talks to and which servers its asks may use — the served commit's
// manifest, never the working tree.
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
    string Url)
{
    // Not in the constructor, so a listing from an apps server that names none reads as asking none.
    public IReadOnlyList<string> Asks { get; init; } = [];

    // All it asks, which an owner approves by saving its space with it.
    [JsonIgnore]
    public AppReach Reach => new() { Reads = Reads, Connect = Connect, Asks = Asks };
}

[PublicAPI]
public sealed record PublishedAppList(IReadOnlyList<PublishedApp> Apps);