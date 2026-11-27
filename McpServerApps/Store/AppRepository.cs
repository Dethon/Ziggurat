using System.Diagnostics;
using System.Text.RegularExpressions;

namespace McpServerApps.Store;

// One built app's git repository, driven through the git CLI in the image. The git directory sits
// beside the working tree, never in it: everything in the tree is the agent's to write, and a git
// directory there would let a write to its hooks or config run code on this server. Every call
// neutralises what could reach git from outside: no system or global config, no hooks, no signing,
// no prompts, a fixed identity, and arguments passed as a list, never a shell line.
public sealed partial class AppRepository(string gitDirectory, string workTree)
{
    private const string Identity = "Ziggurat apps";
    private const string Email = "apps@ziggurat.invalid";
    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(30);

    public string GitDirectory => gitDirectory;
    public string WorkTree => workTree;

    // A file of the working tree by its path from the repository's root, segments already checked: its
    // first limit bytes, enough for the caller to tell one over its bound, or null for none there. No link
    // is followed on the way, a directory's included, since one would read whatever it points at.
    public async Task<byte[]?> TreeFileAsync(string path, int limit, CancellationToken ct)
    {
        var segments = path.Split('/');
        var walked = segments.Select((_, i) => Path.Combine([workTree, .. segments[..(i + 1)]])).ToList();
        if (walked.SkipLast(1).Any(directory => !Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null)
            || !File.Exists(walked[^1]) || new FileInfo(walked[^1]).LinkTarget is not null)
        {
            return null;
        }

        await using var file = File.OpenRead(walked[^1]);
        var buffer = new byte[Math.Min(limit, file.Length)];
        await file.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    public async Task InitAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(workTree);
        await GitAsync(ct, "init", "--quiet", "--template=", "--initial-branch=main");
        // A .gitattributes in the tree is the agent's file; this one, in the git directory, outranks
        // it, so what is committed and served is the bytes that were written.
        Directory.CreateDirectory(Path.Combine(gitDirectory, "info"));
        await File.WriteAllTextAsync(Path.Combine(gitDirectory, "info", "attributes"),
            "* -text -filter -ident -working-tree-encoding\n", ct);
    }

    // Commits whatever the working tree holds now, or answers null when it holds what the last
    // commit holds (there is then nothing new to serve).
    public async Task<string?> CommitAllAsync(string message, CancellationToken ct)
    {
        // Forced: a .gitignore in the tree (an npm project's names dist/) must not keep the build out
        // of what is served. The tree holds nothing the agent did not mean to publish.
        await GitAsync(ct, "add", "--all", "--force");
        var staged = await RunAsync(ct, "diff", "--cached", "--quiet");
        var hasHead = (await RunAsync(ct, "rev-parse", "--verify", "--quiet", "HEAD")).ExitCode == 0;
        if (staged.ExitCode == 0 && hasHead)
        {
            return null;
        }

        await GitAsync(ct, "commit", "--quiet", "--allow-empty", "--no-verify", "-m", message);
        return await HeadAsync(ct);
    }

    public async Task<string?> HeadAsync(CancellationToken ct)
    {
        var head = await RunAsync(ct, "rev-parse", "--verify", "--quiet", "HEAD");
        return head.ExitCode == 0 ? head.Output.Trim() : null;
    }

    // Newest first.
    public async Task<IReadOnlyList<AppCommit>> LogAsync(int limit, CancellationToken ct)
    {
        if (await HeadAsync(ct) is null)
        {
            return [];
        }

        var log = await GitAsync(ct, "log", $"--max-count={limit}", "--format=%H%x09%h%x09%cI%x09%s");
        return
        [
            .. log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', 4))
                .Select(f => new AppCommit(f[0], f[1], DateTimeOffset.Parse(f[2]), f.Length > 3 ? f[3] : ""))
        ];
    }

    // A revision the model wrote, as a commit of this repository, or null. Only a hash (or a prefix of
    // one) or HEAD with a ~N is read, so nothing the model writes is ever an option to git.
    public async Task<AppCommit?> ResolveAsync(string revision, CancellationToken ct)
    {
        if (!Revision().IsMatch(revision))
        {
            return null;
        }

        var resolved = await RunAsync(ct, "rev-parse", "--verify", "--quiet", "--end-of-options", $"{revision}^{{commit}}");
        if (resolved.ExitCode != 0)
        {
            return null;
        }

        var sha = resolved.Output.Trim();
        var shown = await GitAsync(ct, "show", "--no-patch", "--format=%H%x09%h%x09%cI%x09%s", sha);
        var f = shown.Trim().Split('\t', 4);
        return new AppCommit(f[0], f[1], DateTimeOffset.Parse(f[2]), f.Length > 3 ? f[3] : "");
    }

    // What a commit holds at a path relative to the repository's root: a file's bytes as they were
    // committed, a directory, or nothing. One git process, its object named on stdin — never an argument —
    // so nothing in the path is ever read as an option; a path is segments the caller already checked.
    public async Task<CommittedEntry?> EntryAsync(string commit, string path, CancellationToken ct)
    {
        if (!Sha().IsMatch(commit) || path.Any(char.IsControl))
        {
            return null;
        }

        var output = await RunRawAsync($"{commit}:{path}\n", ct, "cat-file", "--batch");
        var newline = Array.IndexOf(output, (byte)'\n');
        var header = newline < 0 ? [] : System.Text.Encoding.UTF8.GetString(output, 0, newline).Split(' ');
        return header switch
        {
            [_, "blob", var size] when int.TryParse(size, out var length) && newline + 1 + length <= output.Length =>
                new CommittedEntry.File(output.AsSpan(newline + 1, length).ToArray()),
            [_, "tree", _] => new CommittedEntry.Directory(),
            _ => null
        };
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();

    [GeneratedRegex("^([0-9a-fA-F]{4,40}|HEAD(~[0-9]{1,4})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex Revision();

    private async Task<string> GitAsync(CancellationToken ct, params string[] args)
    {
        var result = await RunAsync(ct, args);
        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidOperationException($"git {args[0]} failed ({result.ExitCode}): {result.Error.Trim()}");
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(CancellationToken ct, params string[] args)
    {
        using var git = Start(args);
        git.StandardInput.Close();
        return await FinishAsync(git, ct, async (stdout, token) => await stdout.ReadToEndAsync(token));
    }

    // Bytes, never text: a served file is an image as often as a page.
    private async Task<byte[]> RunRawAsync(string input, CancellationToken ct, params string[] args)
    {
        using var git = Start(args);
        await git.StandardInput.WriteAsync(input);
        git.StandardInput.Close();
        var (exitCode, output, error) = await FinishAsync(git, ct, async (stdout, token) =>
        {
            using var bytes = new MemoryStream();
            await stdout.BaseStream.CopyToAsync(bytes, token);
            return bytes.ToArray();
        });
        return exitCode == 0 ? output : throw new InvalidOperationException($"git {args[0]} failed ({exitCode}): {error.Trim()}");
    }

    private static async Task<(int ExitCode, T Output, string Error)> FinishAsync<T>(
        Process git, CancellationToken ct, Func<StreamReader, CancellationToken, Task<T>> read)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        var output = read(git.StandardOutput, deadline.Token);
        var error = git.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await git.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            git.Kill(entireProcessTree: true);
            throw;
        }

        return (git.ExitCode, await output, await error);
    }

    private Process Start(string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false
        };
        foreach (var arg in (string[])
                 [
                     "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgSign=false", "-c", "core.fsmonitor=false",
                     "-c", "core.autocrlf=false", "-c", "core.symlinks=false", "-c", $"user.name={Identity}",
                     "-c", $"user.email={Email}", "--git-dir", gitDirectory, "--work-tree", workTree, .. args
                 ])
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["HOME"] = gitDirectory;

        return Process.Start(start) ?? throw new InvalidOperationException("git could not be started");
    }
}

public sealed record AppCommit(string Sha, string Short, DateTimeOffset At, string Subject);

public abstract record CommittedEntry
{
    public sealed record File(byte[] Bytes) : CommittedEntry;

    public sealed record Directory : CommittedEntry;
}