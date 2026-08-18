using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Files;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

// Driven through the domain filesystem tools rather than against the backend, because that is
// where the rule about which coordinates a response may use lives — a refusal leaking the
// machine's own spelling would pass against the backend and reach the model anyway.
public sealed class JailedOutpostTests : IDisposable
{
    private const string Mount = "/laptop";

    private readonly string _machine = Directory.CreateTempSubdirectory("outpost-jail-").FullName;
    private readonly string _workingDirectory;
    private readonly string _elsewhere;

    public JailedOutpostTests()
    {
        _workingDirectory = Path.Combine(_machine, "project");
        _elsewhere = Path.Combine(_machine, "private");
        Directory.CreateDirectory(_workingDirectory);
        Directory.CreateDirectory(_elsewhere);
        File.WriteAllText(Path.Combine(_workingDirectory, "notes.md"), "a note about herons");
        File.WriteAllText(Path.Combine(_elsewhere, "secrets.md"), "a note about herons");
    }

    public void Dispose() => Directory.Delete(_machine, recursive: true);

    [Fact]
    public async Task AJailedOutpost_ReadsAFileInsideItsWorkingDirectory()
    {
        var result = await new VfsFileReadTool(Registry(jailed: true))
            .RunAsync(Virtual(_workingDirectory, "notes.md"));

        result.ShouldNotBeNull().ShouldBeOk()["content"]!.GetValue<string>().ShouldContain("herons");
    }

    // Above it, elsewhere on the machine by absolute path, and by a spelling that resolves outside
    // it. All three are the same rule; the third is the one a lexical check alone would miss.
    [Fact]
    public async Task AJailedOutpost_RefusesEveryPathOutsideItsWorkingDirectory()
    {
        var tool = new VfsFileReadTool(Registry(jailed: true));

        foreach (var path in Outside())
        {
            (await tool.RunAsync(path)).ShouldNotBeNull()
                .ShouldBeError(ToolError.Codes.InvalidArgument);
        }
    }

    [Fact]
    public async Task AnUnjailedOutpost_AllowsEveryOneOfThem()
    {
        var tool = new VfsFileReadTool(Registry(jailed: false));

        foreach (var path in Outside())
        {
            (await tool.RunAsync(path)).ShouldNotBeNull().ShouldBeOk();
        }
    }

    // The whole reason the refusal exists rather than an empty result: the model has to be able to
    // say which of the two it hit.
    [Fact]
    public async Task ARefusal_SaysItIsARefusalAndNotAnEmptyDirectory()
    {
        var refusal = (await new VfsFileReadTool(Registry(jailed: true))
            .RunAsync(Virtual(_elsewhere, "secrets.md")))!;

        refusal.ShouldBeError(ToolError.Codes.InvalidArgument);
        refusal["message"]!.GetValue<string>().ShouldContain("refusal, not an empty directory");
    }

    // A tool answers in the coordinates it was asked in (ADR 0016), and a refusal is an answer.
    // The message names the path and the working directory under the mount point, never the
    // machine's own spelling of either.
    [Fact]
    public async Task ARefusal_NamesThePathInVirtualCoordinates()
    {
        var refusal = (await new VfsFileReadTool(Registry(jailed: true))
            .RunAsync(Virtual(_elsewhere, "secrets.md")))!;

        var message = refusal["message"]!.GetValue<string>() + refusal["hint"]!.GetValue<string>();
        message.ShouldContain(Virtual(_elsewhere, "secrets.md"));
        message.ShouldContain(Mount + _workingDirectory);
        MachinePathsIn(message).ShouldBeEmpty();
    }

    // Rooted at the working directory rather than filtered afterwards. Walking from / would spend
    // the scan budget on directories it is going to discard and report budgetReached for a reason
    // the model cannot see.
    [Fact]
    public async Task AJailedOutpost_GlobsFromTheWorkingDirectoryWhenNoScopeIsNamed()
    {
        var entries = await GlobAsync(jailed: true, scope: Mount);

        entries.ShouldContain(Virtual(_workingDirectory, "notes.md"));
        entries.ShouldNotContain(Virtual(_elsewhere, "secrets.md"));
    }

    // Scoped to this test's own directory rather than to the mount root, because the machine under
    // it is the real one: an unjailed glob from / is a walk of somebody's whole disk, and it hits
    // the two-hundred-match cap long before it reaches a temp directory. That is the cost the jail
    // avoids by rooting its walk, demonstrated here by having to work around it.
    [Fact]
    public async Task AnUnjailedOutpost_GlobsWhereverItIsPointed()
    {
        var entries = await GlobAsync(jailed: false, scope: Mount + _machine);

        entries.ShouldContain(Virtual(_workingDirectory, "notes.md"));
        entries.ShouldContain(Virtual(_elsewhere, "secrets.md"));
    }

    [Fact]
    public async Task AJailedOutpost_SearchesFromTheWorkingDirectoryWhenNoScopeIsNamed()
    {
        var hits = await SearchAsync(jailed: true, scope: Mount);

        hits.ShouldContain(Virtual(_workingDirectory, "notes.md"));
        hits.ShouldNotContain(Virtual(_elsewhere, "secrets.md"));
    }

    [Fact]
    public async Task AnUnjailedOutpost_SearchesWhereverItIsPointed()
    {
        var hits = await SearchAsync(jailed: false, scope: Mount + _machine);

        hits.ShouldContain(Virtual(_workingDirectory, "notes.md"));
        hits.ShouldContain(Virtual(_elsewhere, "secrets.md"));
    }

    [Fact]
    public async Task AJailedOutpost_RefusesToRemoveSomethingOutsideItsWorkingDirectory()
    {
        var result = await new VfsRemoveTool(Registry(jailed: true))
            .RunAsync(Virtual(_elsewhere, "secrets.md"));

        result.ShouldNotBeNull().ShouldBeError(ToolError.Codes.InvalidArgument);
        File.Exists(Path.Combine(_elsewhere, "secrets.md")).ShouldBeTrue();
    }

    // A transfer off the mount never reaches MoveAsync — it streams the bytes out and then deletes
    // the source — so it has to obey the rule by its own route.
    [Fact]
    public async Task AJailedOutpost_RefusesToLetSomethingOutsideItsWorkingDirectoryLeaveTheMount()
    {
        var backend = Outpost(jailed: true);

        var refused = await backend.MoveOutCheckAsync(Path.Combine(_elsewhere, "secrets.md"), TestCt);
        var allowed = await backend.MoveOutCheckAsync(Path.Combine(_workingDirectory, "notes.md"), TestCt);

        refused.ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Err>()
            .Error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        allowed.ShouldBeOfType<FsResult<FsMoveOutCheckResult>.Ok>();
    }

    private static CancellationToken TestCt => CancellationToken.None;

    private IEnumerable<string> Outside() =>
    [
        Virtual(_elsewhere, "secrets.md"),
        Virtual(_workingDirectory, "../private/secrets.md"),
        Virtual(_machine, "private/secrets.md")
    ];

    private async Task<IReadOnlyList<string>> GlobAsync(bool jailed, string scope)
    {
        var result = await new VfsGlobFilesTool(Registry(jailed)).RunAsync(scope, "**/*.md");
        return [.. result.ShouldNotBeNull().ShouldBeOk()["entries"]!.AsArray().Select(e => e!.GetValue<string>())];
    }

    private async Task<IReadOnlyList<string>> SearchAsync(bool jailed, string scope)
    {
        var result = await new VfsTextSearchTool(Registry(jailed)).RunAsync("herons", directoryPath: scope);
        return
        [
            .. result.ShouldNotBeNull().ShouldBeOk()["results"]!.AsArray()
                .Select(r => r!["file"]!.GetValue<string>())
        ];
    }

    // Every absolute path on this machine that is not spelled under the mount point. A virtual
    // path contains the machine's spelling as a suffix, so the check is that nothing in the text
    // names one without the mount point in front of it.
    private IEnumerable<int> MachinePathsIn(string message) =>
        new[] { _machine, _workingDirectory, _elsewhere }
            .SelectMany(path => Occurrences(message, path))
            .Where(at => at < Mount.Length || message[(at - Mount.Length)..at] != Mount);

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (var at = text.IndexOf(value, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
        {
            yield return at;
        }
    }

    private static string Virtual(string directory, string relative) =>
        $"{Mount}{directory}/{relative}";

    private OutpostFileSystem Outpost(bool jailed) =>
        new("laptop", new LocalFileSystemClient(), _workingDirectory, [".md"], jailed);

    private VirtualFileSystemRegistry Registry(bool jailed)
    {
        var registry = new VirtualFileSystemRegistry();
        var backend = Outpost(jailed);
        registry.Mount(new FileSystemMount("laptop", Mount, backend.DescribeMount), backend);
        return registry;
    }
}