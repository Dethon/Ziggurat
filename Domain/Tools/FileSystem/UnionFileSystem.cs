using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.Files;

namespace Domain.Tools.FileSystem;

// Several filesystems at one mount point, each declaring a FileSystemUnion. Every top-level entry
// belongs to one member: an operation on `<entry>/…` goes to the member whose root listing holds
// `<entry>`, a listing or a search asks every member and merges, and a create of an entry nobody
// holds reads the written content at the members' `routeBy` and goes to whoever claims what is
// there — the fallback where nobody claims anything. Generic: it routes by what the members say
// and knows nothing about what their entries are.
public sealed class UnionFileSystem : IFileSystemBackend, ICallerBoundBackend
{
    public sealed record Member(string Name, FileSystemUnion Union, IFileSystemBackend Backend);

    // Why a mount cannot be a union's member at all: a member is a store of entries, and where a
    // command runs, where a person's files land or what is written for good cannot be a merge.
    public static string? RefusalAsMember(FileSystemMount mount) =>
        mount.Workspace is not null || mount.IsLandingTarget || mount.ShellReach is not null || mount.OccupiedNames is not null
            ? $"'{mount.Name}' declares a workspace, a shell or a landing target, which a filesystem sharing a mount point cannot"
            : null;

    public abstract record Joining
    {
        private Joining() { }

        public sealed record Joined(FileSystemMount Mount, UnionFileSystem Backend) : Joining;

        public sealed record Refused(string Reason) : Joining;
    }

    // The newcomer joins the mount already at its mount point, which may itself be a union. Both
    // must declare a union; the members must agree on where the routing value is read, and no value
    // nor the fallback may be two members', or a create could not be routed.
    public static Joining Join(
        (FileSystemMount Mount, IFileSystemBackend Backend) held,
        (FileSystemMount Mount, IFileSystemBackend Backend) newcomer)
    {
        if (held.Mount.Union is not { } heldUnion || newcomer.Mount.Union is not { } union)
        {
            return new Joining.Refused("only filesystems that all declare a union share a mount point");
        }

        if ((RefusalAsMember(newcomer.Mount) ?? RefusalAsMember(held.Mount)) is { } refusal)
        {
            return new Joining.Refused(refusal);
        }

        var existing = held.Backend is UnionFileSystem joined
            ? joined._members
            : [new Member(held.Mount.Name, heldUnion, held.Backend)];

        if (existing.FirstOrDefault(m => m.Union.RouteBy != union.RouteBy) is { } other)
        {
            return new Joining.Refused(
                $"'{newcomer.Mount.Name}' routes by '{union.RouteBy}' and '{other.Name}' by '{other.Union.RouteBy}'");
        }

        if (union.Fallback && existing.FirstOrDefault(m => m.Union.Fallback) is { } fallback)
        {
            return new Joining.Refused($"'{fallback.Name}' is already the fallback at {held.Mount.MountPoint}");
        }

        var taken = existing
            .SelectMany(m => m.Union.Claims.Intersect(union.Claims, StringComparer.Ordinal).Select(v => $"'{v}' ({m.Name})"))
            .ToList();
        if (taken.Count > 0)
        {
            return new Joining.Refused($"values already claimed: {string.Join(", ", taken)}");
        }

        var members = existing.Append(new Member(newcomer.Mount.Name, union, newcomer.Backend)).ToList();
        var capabilities = held.Mount.Capabilities.Union(newcomer.Mount.Capabilities).ToHashSet(StringComparer.Ordinal);

        return new Joining.Joined(
            new FileSystemMount(
                string.Join("+", members.Select(m => m.Name)),
                held.Mount.MountPoint,
                string.Join(" ", new[] { held.Mount.Description, newcomer.Mount.Description }.Where(d => d.Length > 0)))
            {
                Capabilities = FileSystemOperations.All
                    .Where(o => o.Capability is not null && capabilities.Contains(o.Capability))
                    .Select(o => o.Capability!)
                    .ToList(),
                Union = new FileSystemUnion(
                    union.RouteBy,
                    [.. members.SelectMany(m => m.Union.Claims)],
                    members.Any(m => m.Union.Fallback))
            },
            new UnionFileSystem(members));
    }

    private readonly IReadOnlyList<Member> _members;

    public UnionFileSystem(IReadOnlyList<Member> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentOutOfRangeException.ThrowIfZero(members.Count);
        _members = members;
    }

    public IReadOnlyList<Member> Members => _members;

    public string FilesystemName => string.Join("+", _members.Select(m => m.Name));

    // Every member as this caller sees it, so each tells its own server who is calling: a watch
    // records the conversation that wrote it whichever member keeps it.
    public IFileSystemBackend As(ConversationContext? caller) =>
        new UnionFileSystem([.. _members.Select(m => m with
        {
            Backend = m.Backend is ICallerBoundBackend bound ? bound.As(caller) : m.Backend
        })]);

    private Member? Fallback => _members.FirstOrDefault(m => m.Union.Fallback);

    // What answers for the union's root itself, which no member holds.
    private Member Root => Fallback ?? _members[0];

    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var answers = await AskAllAsync(m => m.Backend.GlobAsync(basePath, pattern, ct));
        var listed = answers.Where(a => a.Value is not null).Select(a => a.Value!).ToList();
        var unavailable = answers.Where(a => a.Unavailable).Select(a => a.Member.Name).ToList();

        if (listed.Count == 0 && unavailable.Count == 0)
        {
            return new FsResult<FsGlobResult>.Err(answers.First(a => a.Error is not null).Error!);
        }

        var entries = listed.SelectMany(g => g.Entries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var kept = entries.Take(GlobFilesTool.FileResultCap).ToList();
        var executables = listed.SelectMany(g => g.Executables ?? []).Intersect(kept, StringComparer.Ordinal).ToList();
        var notes = listed.Select(g => g.Note).OfType<string>().ToList();

        return new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = kept,
            Truncated = listed.Any(g => g.Truncated) || entries.Count > kept.Count,
            Total = listed.Sum(g => g.Total),
            EntriesScanned = listed.Sum(g => g.EntriesScanned),
            BudgetReached = listed.Any(g => g.BudgetReached),
            Executables = executables.Count == 0 ? null : executables,
            Note = notes.Count == 0 ? null : string.Join(" ", notes),
            Unavailable = unavailable.Count == 0 ? null : unavailable
        });
    }

    public async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        var answers = await AskAllAsync(m =>
            m.Backend.SearchAsync(query, regex, path, directoryPath, filePattern, maxResults, contextLines, outputMode, ct));
        var found = answers.Where(a => a.Value is not null).Select(a => a.Value!).ToList();
        if (found.Count == 0)
        {
            return new FsResult<FsSearchResult>.Err(answers.First(a => a.Error is not null).Error!);
        }

        // Capped as one mount's search is: at maxResults matches, a file's worth at a time.
        var results = found.SelectMany(s => s.Results).OrderBy(r => r.File, StringComparer.Ordinal).ToList();
        var counted = results.Select(r => (Result: r, Matches: r.MatchCount ?? r.Matches?.Count ?? 1)).ToList();
        var kept = counted
            .Where((r, i) => counted.Take(i).Sum(c => c.Matches) < maxResults)
            .ToList();

        return new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = found[0].Path,
            FilesSearched = found.Sum(s => s.FilesSearched),
            FilesWithMatches = kept.Count,
            TotalMatches = kept.Sum(r => r.Matches),
            Truncated = found.Any(s => s.Truncated) || kept.Count < counted.Count,
            EntriesScanned = found.Sum(s => s.EntriesScanned),
            BudgetReached = found.Any(s => s.BudgetReached),
            Results = [.. kept.Select(r => r.Result)]
        });
    }

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Entry(path).Length == 0
            ? new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true })
            : await OnHolderAsync(path, b => b.InfoAsync(path, ct), ct);

    public Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        OnHolderAsync(path, b => b.ReadAsync(path, offset, limit, ct), ct);

    public Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        OnHolderAsync(path, b => b.EditAsync(path, edits, ct), ct);

    public Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        OnHolderAsync(path, b => b.DeleteAsync(path, ct), ct);

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        OnHolderAsync(path, b => b.ExecAsync(path, command, timeoutSeconds, ct), ct);

    public Task<FsResult<FsQueryResult>> QueryAsync(string path, string sql, int maxRows, CancellationToken ct) =>
        OnHolderAsync(path, b => b.QueryAsync(path, sql, maxRows, ct), ct);

    public Task<FsResult<FsMoveOutCheckResult>> MoveOutCheckAsync(string path, CancellationToken ct) =>
        OnHolderAsync(path, b => b.MoveOutCheckAsync(path, ct), ct);

    public async Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
        bool createDirectories, CancellationToken ct)
    {
        var target = await ForNewAsync(path, content, ct);
        return target.Member is { } member
            ? await member.Backend.CreateAsync(path, content, overwrite, createDirectories, ct)
            : new FsResult<FsCreateResult>.Err(target.Error!);
    }

    // Within one member's entries an operation of that member's; between two members' it would be a
    // write one member makes and another loses, which no member can do, so it is refused rather than
    // half done. A destination nobody holds is the source holder's new entry.
    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        BetweenAsync(sourcePath, destinationPath, b => b.MoveAsync(sourcePath, destinationPath, ct), ct);

    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath, bool overwrite,
        bool createDirectories, CancellationToken ct) =>
        BetweenAsync(sourcePath, destinationPath, b => b.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct), ct);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var placed = await PlaceAsync(path, ct);
        if (placed.Error is { } error)
        {
            throw new FileSystemOperationException(error);
        }

        await foreach (var chunk in placed.Member!.Backend.ReadChunksAsync(path, ct))
        {
            yield return chunk;
        }
    }

    // Bytes route as text does, by what they hold, so they are read whole first: a union's members
    // are stores of small entries, and the bridge that writes here holds a file whole already.
    public async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            bytes.Write(chunk.Span);
        }

        var content = bytes.ToArray();
        var target = await ForNewAsync(path, System.Text.Encoding.UTF8.GetString(content), ct);
        return target.Member is { } member
            ? await member.Backend.WriteChunksAsync(path, One(content), overwrite, createDirectories, ct)
            : throw new FileSystemOperationException(target.Error!);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> One(byte[] content)
    {
        await Task.CompletedTask;
        yield return content;
    }

    private async Task<FsResult<T>> OnHolderAsync<T>(
        string path, Func<IFileSystemBackend, Task<FsResult<T>>> operation, CancellationToken ct) where T : class
    {
        var placed = await PlaceAsync(path, ct);
        return placed.Member is { } member ? await operation(member.Backend) : new FsResult<T>.Err(placed.Error!);
    }

    private async Task<FsResult<T>> BetweenAsync<T>(
        string sourcePath, string destinationPath, Func<IFileSystemBackend, Task<FsResult<T>>> operation,
        CancellationToken ct) where T : class
    {
        var listing = await ListRootsAsync(ct);
        var source = listing.Place(Entry(sourcePath), this);
        if (source.Member is not { } member)
        {
            return new FsResult<T>.Err(source.Error!);
        }

        return listing.HolderOf(Entry(destinationPath)) is { } other && other != member
            ? FsError.Invalid<T>(
                $"{sourcePath} is {member.Name}'s and {destinationPath} is {other.Name}'s: an entry cannot move "
                + "or be copied from one filesystem sharing this mount point to another. Read it and write it anew instead.")
            : await operation(member.Backend);
    }

    private async Task<Placement> PlaceAsync(string path, CancellationToken ct)
    {
        var entry = Entry(path);
        return entry.Length == 0 ? new Placement(Root, null) : (await ListRootsAsync(ct)).Place(entry, this);
    }

    // The member a new entry belongs to: its holder where one already holds it, otherwise whoever
    // claims what the content holds at the pointer, the fallback where it holds nothing claimed.
    private async Task<Placement> ForNewAsync(string path, string content, CancellationToken ct)
    {
        var entry = Entry(path);
        if (entry.Length == 0)
        {
            return new Placement(Root, null);
        }

        if ((await ListRootsAsync(ct)).HolderOf(entry) is { } holder)
        {
            return new Placement(holder, null);
        }

        var values = ValuesAt(content, _members[0].Union.RouteBy);
        var owners = values
            .Select(v => (Value: v, Owner: _members.FirstOrDefault(m => m.Union.Claims.Contains(v, StringComparer.Ordinal))))
            .ToList();
        var targets = owners.Select(o => o.Owner ?? Fallback).Distinct().ToList();

        return targets switch
        {
            [] when Fallback is { } fallback => new Placement(fallback, null),
            [{ } only] => new Placement(only, null),
            _ => new Placement(null, Unroutable(path, owners))
        };
    }

    private ToolErrorResult Unroutable(string path, IReadOnlyList<(string Value, Member? Owner)> owners)
    {
        var routeBy = _members[0].Union.RouteBy;
        var each = owners
            .GroupBy(o => o.Owner?.Name)
            .Select(g => $"{string.Join(", ", g.Select(o => $"'{o.Value}'").Distinct())} → "
                         + (g.Key ?? (Fallback is { } f ? $"{f.Name} (claimed by nobody)" : "nothing here (claimed by nobody)")));
        var claims = string.Join("; ", _members.Select(m => m.Union.Fallback
            ? $"{m.Name} takes what nobody claims"
            : $"{m.Name} claims {string.Join(", ", m.Union.Claims.Select(c => $"'{c}'"))}"));
        return new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.InvalidArgument,
            Message = owners.Count == 0
                ? $"{path} cannot be created: it holds nothing at {routeBy}, and no filesystem sharing this mount "
                  + "point takes what nobody claims."
                : $"{path} cannot be created: the values at {routeBy} belong to different filesystems sharing "
                  + $"this mount point ({string.Join("; ", each)}). One entry is kept by one filesystem.",
            Hint = $"Write one entry per filesystem. Here {claims}."
        };
    }

    // The values at a JSON pointer, `*` standing for every item of an array. Content that is not
    // JSON, or holds nothing there, has none. A value that is not a string is spelled as JSON, which
    // claims nothing a member names by word.
    internal static IReadOnlyList<string> ValuesAt(string content, string pointer)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return [];
        }

        var tokens = pointer.Split('/').Skip(1).Select(t => t.Replace("~1", "/").Replace("~0", "~"));
        return tokens
            .Aggregate(
                root is null ? [] : new[] { root }.AsEnumerable(),
                (nodes, token) => nodes.SelectMany(node => Step(node, token)))
            .Select(node => node is JsonValue value && value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : node.ToJsonString())
            .ToList();
    }

    private static IEnumerable<JsonNode> Step(JsonNode node, string token) => node switch
    {
        JsonArray array when token == "*" => array.OfType<JsonNode>(),
        JsonArray array when int.TryParse(token, out var index) && index >= 0 && index < array.Count && array[index] is { } item => [item],
        JsonObject obj when obj.TryGetPropertyValue(token, out var child) && child is not null => [child],
        _ => []
    };

    private static string Entry(string path) => path.Trim('/').Split('/')[0];

    // The name an entry of a root listing goes by, whatever spelling the member answers in: the
    // listing is of the root, so its last segment is the entry.
    private static string NameOf(string listed) => listed.TrimEnd('/').Split('/')[^1];

    private async Task<RootListing> ListRootsAsync(CancellationToken ct) =>
        new(await AskAllAsync(m => m.Backend.GlobAsync("", "*", ct)));

    private sealed record RootListing(IReadOnlyList<Answer<FsGlobResult>> Answers)
    {
        public Member? HolderOf(string entry) =>
            Answers.FirstOrDefault(a => a.Value?.Entries.Any(e => NameOf(e) == entry) == true)?.Member;

        // An entry nobody holds is answered by the root's member (its not-found), unless a member
        // that may hold it did not answer: then it is that member being away, not the entry missing.
        public Placement Place(string entry, UnionFileSystem union)
        {
            if (HolderOf(entry) is { } holder)
            {
                return new Placement(holder, null);
            }

            var away = Answers.Where(a => a.Unavailable).Select(a => a.Member.Name).ToList();
            return away.Count == 0
                ? new Placement(union.Root, null)
                : new Placement(null, new ToolErrorResult
                {
                    ErrorCode = ToolError.Codes.TransientDependency,
                    Message = $"'{entry}' was not found among the filesystems that answered, and "
                              + $"{string.Join(", ", away)} did not answer, so it may be there.",
                    Hint = "Tell the user that part is unreachable for now rather than that the entry does not exist."
                });
        }
    }

    private sealed record Placement(Member? Member, ToolErrorResult? Error);

    private sealed record Answer<T>(Member Member, T? Value, ToolErrorResult? Error, bool Unavailable) where T : class;

    // A member that refuses (the caller, the path, the operation) answers nothing and fails nothing;
    // one that throws or fails in a way worth retrying is away, and named so.
    private async Task<IReadOnlyList<Answer<T>>> AskAllAsync<T>(Func<Member, Task<FsResult<T>>> ask) where T : class =>
        await Task.WhenAll(_members.Select(async member =>
        {
            try
            {
                return (await ask(member)).TryGetValue(out var value, out var error)
                    ? new Answer<T>(member, value, null, false)
                    : new Answer<T>(member, null, error, error.Retryable);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new Answer<T>(member, null, new ToolErrorResult
                {
                    ErrorCode = ToolError.Codes.TransientDependency,
                    Message = $"{member.Name} did not answer: {ex.Message}"
                }, true);
            }
        }));
}