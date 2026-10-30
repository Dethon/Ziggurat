using System.Text.Json;
using System.Text.RegularExpressions;
using HouseholdMigration.Steps;

namespace HouseholdMigration;

// One person of the retired identity map, as the operator copies them from prod's
// IDENTITY__PEOPLE__<n>__{NAME,TELEGRAM,WEBCHAT,VOICE}, plus what the map never held: the numeric
// Telegram id the bot now knows people by, and any other name their memories or schedules were
// filed under (a Telegram first name, a satellite's identity, a name spelled two ways).
public sealed record PersonEntry
{
    // The map's Name: the person's key in this run and the next, and the name their remembered web
    // logins were sealed under.
    public string Name { get; init; } = "";

    // What they will go by; the map's Name when absent.
    public string? DisplayName { get; init; }

    public string? Telegram { get; init; }

    public long? TelegramId { get; init; }

    public string? WebChat { get; init; }

    // Their speaker profile's folder under /voices.
    public string? Voice { get; init; }

    public IReadOnlyList<string> OldNames { get; init; } = [];

    // The picture the retired chat client showed beside them (its Users list's AvatarUrl, a path such as
    // /avatars/kakera.png on the chat client's host), which becomes their account's unless it shows one.
    public string? AvatarUrl { get; init; }

    // owner or member; the people named the operator and the owner are owners whatever this says.
    public string? Role { get; init; }

    public string ShownAs => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName.Trim();

    // Every name something of theirs may be filed under. Redis keys are case-sensitive, so each
    // spelling counts on its own.
    public IReadOnlyList<string> Names =>
    [
        .. new[] { Name, Telegram, WebChat, Voice }.Concat(OldNames)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.Ordinal)
    ];
}

public sealed partial record PeopleFile
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    public IReadOnlyList<PersonEntry> People { get; init; } = [];

    public static PeopleFile Read(string path) =>
        JsonSerializer.Deserialize<PeopleFile>(File.ReadAllText(path), _json) ?? new PeopleFile();

    // What would make the run guess, said before anything is touched.
    public IReadOnlyList<string> Problems(params string[] named) =>
    [
        .. People.Count == 0 ? ["the file lists nobody"] : Array.Empty<string>(),
        .. People.Where(p => string.IsNullOrWhiteSpace(p.Name)).Select(_ => "somebody has no name"),
        .. People.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is listed twice"),
        .. named.Where(n => People.All(p => p.Name != n)).Select(n => $"{n} is not one of the people listed"),
        .. People.SelectMany(p => p.Names.Select(n => (Person: p.Name, Old: n)))
            .GroupBy(x => x.Old, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Person).Distinct().Count() > 1)
            .Select(g => $"'{g.Key}' is a name of {string.Join(" and ", g.Select(x => x.Person).Distinct())}"),
        .. People.SelectMany(p => p.Names).Where(n => Unsafe().IsMatch(n) || n == ProfilesSegment)
            .Select(n => $"'{n}' cannot be a name: a Redis pattern or a folder would read it as something else"),
        .. People.SelectMany(p => p.Names).Where(n => MemoryBuckets.Owners.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Select(n => $"'{n}' is the household's, not a person's name"),
        .. People.Where(p => p.Role is not null and not ("owner" or "member"))
            .Select(p => $"{p.Name}'s role is '{p.Role}', which is neither owner nor member")
    ];

    // memory:profile:{name} is every person's profile, so memories "under" this name would be all of them.
    private const string ProfilesSegment = "profile";

    // A glob character would match other people's keys, and a path separator would leave /voices.
    [GeneratedRegex(@"[*?\[\]:/\\]|^\.\.?$")]
    private static partial Regex Unsafe();
}