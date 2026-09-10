using System.Diagnostics;
using Domain.Prompts;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The changed tier's selection: what the working tree differs in from a ref, read once per
// process and handed to the scenario classes as their rows. Off unless asked for, so a bare run
// spends nothing and the tier's classes hold one row that skips.
public static class EvalChanges
{
    public const string Variable = "ZIGGURAT_EVAL_CHANGED";

    // The one row a shard holds when nothing is selected: a theory with no rows fails discovery,
    // and a row that skips is a tier that is present and idle.
    public const string NothingSelected = "(nothing selected)";

    private const string MainBranch = "master";

    private static readonly Lazy<IReadOnlyList<Scenario>> _selected = new(() =>
        Reference(Environment.GetEnvironmentVariable(Variable)) is { } reference
            ? ChangeScope.Select(
                Files(reference),
                path => Read(Path.Combine(RepositoryRoot.Path, path)),
                EvalSuite.All, PromptManifest.Claims)
            : []);

    public static IReadOnlyList<Scenario> Selected => _selected.Value;

    // The ref the working tree is compared against: the main branch when merely asked for, or
    // whichever one is named. The comparison is against the merge base, so a branch's own
    // commits count as changes and the main branch's newer ones do not.
    public static string? Reference(string? asked) =>
        asked?.Trim() switch
        {
            null or "" or "0" => null,
            "1" or "true" => MainBranch,
            var named => named
        };

    public static IReadOnlyList<string> Parsed(string diff, string untracked) =>
        [.. diff.Split('\n').Concat(untracked.Split('\n'))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct()];

    private static IReadOnlyList<string> Files(string reference)
    {
        var mergeBase = Git("merge-base", reference, "HEAD").Trim();
        return Parsed(
            Git("diff", "--name-only", mergeBase),
            Git("ls-files", "--others", "--exclude-standard"));
    }

    private static string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start)
            ?? throw new InvalidOperationException("git could not be started");
        var output = git.StandardOutput.ReadToEnd();
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();

        return git.ExitCode == 0
            ? output
            : throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: {error.Trim()}");
    }

    private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}