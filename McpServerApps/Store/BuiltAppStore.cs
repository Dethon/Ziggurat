using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Apps;
using Domain.DTOs.Channel;
using McpServerApps.Settings;

namespace McpServerApps.Store;

// What the apps server keeps about a built app beside its repository, never in it: the repository is
// the agent's to write, and who built it, for whom and which commit is served are not.
public sealed record BuiltApp
{
    public required string Id { get; init; }
    public required string Slug { get; init; }
    public required string HouseholdId { get; init; }

    // The person whose turn wrote its app.json first: only their turns see or change it.
    public required Account Author { get; init; }

    // The household agent whose turn created it, which the app reads through later.
    public required string BuildingAgent { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    // What the app serves; null until the first publish.
    public string? PublishedCommit { get; init; }
}

// The volume: `<storage>/<household>/<id>/` holds an app's metadata (`app.meta.json`), its git
// directory (`git/`) and its working tree (`tree/`). Repositories are named by id, never by slug, so
// a renamed app keeps its links. Creating, renaming, deleting and publishing run one at a time per
// household, so a quota or a slug is never decided on a listing another call is changing.
public sealed class BuiltAppStore(AppsSettings settings, TimeProvider clock)
{
    private const string MetadataFile = "app.meta.json";
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _households = new(StringComparer.Ordinal);

    public AppQuotas Quotas => settings.Quotas;

    public async Task<T> OneAtATimeAsync<T>(string householdId, Func<Task<T>> work, CancellationToken ct)
    {
        var gate = _households.GetOrAdd(householdId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<BuiltApp>> ListAsync(string householdId, CancellationToken ct)
    {
        var household = HouseholdDirectory(householdId);
        if (!Directory.Exists(household))
        {
            return [];
        }

        var apps = await Task.WhenAll(Directory.EnumerateDirectories(household)
            .Select(app => Path.Combine(app, MetadataFile))
            .Where(File.Exists)
            .Select(async file =>
            {
                await using var stream = File.OpenRead(file);
                return await JsonSerializer.DeserializeAsync<BuiltApp>(stream, _json, ct);
            }));
        return [.. apps.OfType<BuiltApp>().OrderBy(app => app.Slug, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<BuiltApp>> ListAsync(string householdId, Account author, CancellationToken ct) =>
        [.. (await ListAsync(householdId, ct)).Where(app => app.Author == author)];

    // A new app: an id no household's app has, its repository initialised, its manifest written into
    // the working tree, then its metadata — the last step, so a failure leaves no app half-listed.
    public async Task<BuiltApp> CreateAsync(
        string householdId, Account author, string buildingAgent, string slug, string manifest, CancellationToken ct)
    {
        var id = Enumerable.Range(0, 16).Select(_ => BuiltAppId.Mint())
            .First(candidate => !Directory.Exists(Root)
                                || !Directory.EnumerateDirectories(Root).Any(household => Directory.Exists(Path.Combine(household, candidate))));
        var app = new BuiltApp
        {
            Id = id,
            Slug = slug,
            HouseholdId = householdId,
            Author = author,
            BuildingAgent = buildingAgent,
            CreatedAt = clock.GetUtcNow()
        };

        var repository = Repository(app);
        await repository.InitAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(repository.WorkTree, AppManifest.FileName), manifest, ct);
        await SaveAsync(app, ct);
        return app;
    }

    public async Task SaveAsync(BuiltApp app, CancellationToken ct)
    {
        var file = Path.Combine(AppDirectory(app), MetadataFile);
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(app, _json), ct);
        File.Move(temp, file, overwrite: true);
    }

    // The metadata goes first, so an app half-deleted by a crash is no longer listed.
    public Task DeleteAsync(BuiltApp app)
    {
        var directory = AppDirectory(app);
        File.Delete(Path.Combine(directory, MetadataFile));
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    public AppRepository Repository(BuiltApp app) =>
        new(Path.Combine(AppDirectory(app), "git"), Path.Combine(AppDirectory(app), "tree"));

    // The repository as a whole: its working tree and its history.
    public long SizeOf(BuiltApp app) =>
        new DirectoryInfo(AppDirectory(app)).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    private string Root => Path.GetFullPath(settings.StoragePath);

    private string AppDirectory(BuiltApp app) => Path.Combine(HouseholdDirectory(app.HouseholdId), app.Id);

    // A household's id is the accounts service's opaque one, taken from a verified token; it is still
    // never trusted to be a single path segment.
    private string HouseholdDirectory(string householdId) =>
        householdId is { Length: > 0 } && householdId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? Path.Combine(Root, householdId)
            : throw new ArgumentException("A household id is letters, digits, '-' and '_'.", nameof(householdId));
}