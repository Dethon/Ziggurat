using System.Diagnostics;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Deployment;

// A script the docs say to run as `scripts/…` and the format hook git runs before every commit are
// executable in the index itself: this repository is checked out with core.fileMode false, so a
// chmod on disk is never recorded, and a fresh clone or worktree got them as plain files — the
// hook was skipped with a hint and a commit went out unformatted.
public sealed class ExecutableScriptsTests
{
    [Fact]
    public async Task EveryTrackedScriptWithAShebang_IsExecutableInTheIndex()
    {
        var root = TestHelpers.FindSolutionRoot();
        using var git = Process.Start(new ProcessStartInfo("git", "ls-files -s -- .githooks scripts")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false
        })!;
        var listing = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();

        var scripts = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (Mode: line.Split(' ')[0], Path: line.Split('\t')[1]))
            .Where(f => File.Exists(Path.Combine(root, f.Path))
                        && File.ReadLines(Path.Combine(root, f.Path)).FirstOrDefault()?.StartsWith("#!") == true)
            .ToList();

        scripts.ShouldNotBeEmpty();
        scripts.Where(s => s.Mode != "100755").Select(s => s.Path).ShouldBeEmpty();
    }
}