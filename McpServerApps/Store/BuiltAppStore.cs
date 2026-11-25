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

    // The outside code each published commit's pages were found pinned with at its publish, by commit: all
    // the served policy lets them load from a CDN. A rollback moves only PublishedCommit, so each commit
    // keeps its own; a commit with none recorded loads no outside code.
    public IReadOnlyDictionary<string, PinnedCode> Pinned { get; init; } = new Dictionary<string, PinnedCode>();

    public PinnedCode PinnedAt(string commit) => Pinned.GetValueOrDefault(commit) ?? PinnedCode.None;
}

// The volume: `<storage>/<household>/<id>/` holds an app's metadata (`app.meta.json`), its git
// directory (`git/`), its working tree (`tree/`) and what its checks saw (`checks/`). Repositories are named by id, never by slug, so
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

    // An app by its id alone, whichever household holds it: what an app origin's label names.
    public async Task<BuiltApp?> FindAsync(string id, CancellationToken ct)
    {
        if (!BuiltAppId.IsValid(id) || !Directory.Exists(Root))
        {
            return null;
        }

        var file = Directory.EnumerateDirectories(Root)
            .Select(household => Path.Combine(household, id, MetadataFile))
            .FirstOrDefault(File.Exists);
        if (file is null)
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<BuiltApp>(stream, _json, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between finding and reading: no app.
            return null;
        }
    }

    // A deleted app leaves its id behind, so its origin can still say it was removed (AppPages) and
    // no later app is ever handed that origin, with whatever the first left in browsers there.
    public bool IsRemoved(string id) => BuiltAppId.IsValid(id) && File.Exists(Path.Combine(Removed, id));

    // Held by a live app in any household, or by a removed one.
    public bool IdTaken(string id) =>
        IsRemoved(id)
        || (Directory.Exists(Root) && Directory.EnumerateDirectories(Root).Any(household => Directory.Exists(Path.Combine(household, id))));

    public async Task<IReadOnlyList<BuiltApp>> ListAsync(string householdId, Account author, CancellationToken ct) =>
        [.. (await ListAsync(householdId, ct)).Where(app => app.Author == author)];

    // A new app: an id no household's app has, its repository initialised, its manifest written into
    // the working tree, then its metadata — the last step, so a failure leaves no app half-listed.
    public async Task<BuiltApp> CreateAsync(
        string householdId, Account author, string buildingAgent, string slug, string manifest, CancellationToken ct)
    {
        var id = Enumerable.Range(0, 16).Select(_ => BuiltAppId.Mint()).First(candidate => !IdTaken(candidate));
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

    // The tombstone first, then the metadata, so an app half-deleted by a crash is no longer listed
    // and its id is already kept from every later app.
    public Task DeleteAsync(BuiltApp app)
    {
        var directory = AppDirectory(app);
        Directory.CreateDirectory(Removed);
        File.WriteAllText(Path.Combine(Removed, app.Id), clock.GetUtcNow().ToString("O"));
        File.Delete(Path.Combine(directory, MetadataFile));
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    // The manifest the app serves: the served commit's, never the working tree's; none until the first
    // publish, nor for a commit whose manifest no longer reads.
    public async Task<AppManifest?> PublishedManifestAsync(BuiltApp app, CancellationToken ct) =>
        app.PublishedCommit is { } commit
        && await Repository(app).EntryAsync(commit, AppManifest.FileName, ct) is CommittedEntry.File { Bytes: var json }
        && AppManifest.Parse(System.Text.Encoding.UTF8.GetString(json)) is AppManifestParse.Valid { Manifest: var manifest }
            ? manifest
            : null;

    public AppRepository Repository(BuiltApp app) =>
        new(Path.Combine(AppDirectory(app), "git"), Path.Combine(AppDirectory(app), "tree"));

    // What the app's checks saw, beside its repository and never in it: inside the working tree a publish
    // would commit the screenshots and the preview serve them. The mount shows it read-only as
    // `/<slug>/.checks/`.
    public string ChecksDirectory(BuiltApp app) => Path.Combine(AppDirectory(app), "checks");

    // The runtime errors its pages reported (Serving/AppErrorLog), beside the repository for the same reason,
    // newest first. The mount shows it read-only as `/<slug>/errors`.
    public string ErrorsFile(BuiltApp app) => Path.Combine(AppDirectory(app), "errors");

    // The repository as a whole: its working tree and its history. A check's output and the reported errors
    // are the server's, bounded by the server, and spend nothing of the app's.
    public long SizeOf(BuiltApp app)
    {
        var checks = ChecksDirectory(app) + Path.DirectorySeparatorChar;
        var errors = ErrorsFile(app);
        return new DirectoryInfo(AppDirectory(app)).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !file.FullName.StartsWith(checks, StringComparison.Ordinal)
                           && !file.FullName.StartsWith(errors, StringComparison.Ordinal))
            .Sum(file => file.Length);
    }

    private string Root => Path.GetFullPath(settings.StoragePath);

    // Beside the households, under a name no household id can take (ids carry no dot).
    private string Removed => Path.Combine(Root, ".removed");

    private string AppDirectory(BuiltApp app) => Path.Combine(HouseholdDirectory(app.HouseholdId), app.Id);

    // A household's id is the accounts service's opaque one, taken from a verified token; it is still
    // never trusted to be a single path segment.
    private string HouseholdDirectory(string householdId) =>
        householdId is { Length: > 0 } && householdId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? Path.Combine(Root, householdId)
            : throw new ArgumentException("A household id is letters, digits, '-' and '_'.", nameof(householdId));
}