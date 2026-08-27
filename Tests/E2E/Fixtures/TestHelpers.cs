using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Tests.E2E.Fixtures;

internal sealed record DockerBuildCommand(
    string FileName,
    string Arguments,
    IReadOnlyDictionary<string, string> Environment);

internal static class TestHelpers
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _imageLocks = new();

    internal static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find solution root directory.");
    }

    internal static Task EnsureBaseSdkImageAsync(string solutionRoot, CancellationToken ct) =>
        EnsureImageAsync(solutionRoot, E2EImages.BaseSdk, ct);

    internal static Task EnsureImageAsync(string solutionRoot, E2EImageSpec spec, CancellationToken ct) =>
        EnsureImageAsync(solutionRoot, spec.Dockerfile, spec.ImageName, spec.WatchedDirs, ct);

    internal static async Task EnsureImageAsync(
        string solutionRoot,
        string dockerfile,
        string imageName,
        IReadOnlyList<string> watchedDirs,
        CancellationToken ct)
    {
        var gate = _imageLocks.GetOrAdd(imageName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // The semaphore above only serialises threads within this process. Separate
            // processes (E2E vs benchmark as distinct `dotnet test` jobs, or a concurrent
            // `docker compose build`) would otherwise duplicate the same build, and a leaf
            // build could start while base-sdk:latest is still being re-tagged under its
            // own `FROM base-sdk:latest`.
            await using var fileLock = await AcquireImageFileLockAsync(imageName, ct);

            var newestSource = GetNewestSourceTimestamp(solutionRoot, watchedDirs, dockerfile);
            var imageExists = await DockerImageExistsAsync(imageName, ct);
            if (IsImageFresh(imageExists, ReadImageBuildStamp(imageName), newestSource))
            {
                return;
            }

            var elapsed = Stopwatch.StartNew();
            await RunDockerBuildAsync(solutionRoot, dockerfile, imageName, ct);
            RecordImageBuild(imageName, newestSource);
            Console.WriteLine($"[e2e] built {imageName} in {elapsed.Elapsed.TotalSeconds:0.0}s");
        }
        finally
        {
            gate.Release();
        }
    }

    // Driving the docker CLI rather than Testcontainers' ImageFromDockerfileBuilder is
    // deliberate. The builder posts to the Engine /build endpoint, which serves the legacy
    // builder: its layer cache is disjoint from the one docker compose and buildx use, so
    // alternating between `docker compose up --build` and the tests meant a cold rebuild every
    // time, and the legacy builder ignores the --mount=type=cache NuGet mounts. Asking
    // Testcontainers for BuildKit (ImageBuildParameters.Version = "2") is not an option:
    // Docker.DotNet.Enhanced types JSONMessage.Aux as an object, BuildKit sends it as a base64
    // string, and the build throws while deserialising its own progress stream.
    internal static DockerBuildCommand CreateBuildCommand(
        string solutionRoot,
        string dockerfile,
        string imageName) =>
        new(
            "docker",
            $"build --file \"{Path.Combine(solutionRoot, dockerfile)}\" --tag \"{imageName}\" "
            + $"--progress plain \"{solutionRoot}\"",
            new Dictionary<string, string> { ["DOCKER_BUILDKIT"] = "1" });

    private static async Task RunDockerBuildAsync(
        string solutionRoot,
        string dockerfile,
        string imageName,
        CancellationToken ct)
    {
        var command = CreateBuildCommand(solutionRoot, dockerfile, imageName);
        var psi = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = solutionRoot
        };
        foreach (var (key, value) in command.Environment)
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start docker to build {imageName}.");

        // BuildKit writes its progress to stderr. Drain both pipes concurrently, or a build
        // chatty enough to fill one blocks forever while we wait on the other.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            // Both reads have to be observed before the `using` disposes the process out from
            // under them, or the timeout path leaves two faulted tasks nobody ever looks at.
            await DrainAsync(stdout, stderr);
            throw;
        }

        var output = string.Join(Environment.NewLine, await stdout, await stderr);
        if (process.ExitCode == 0)
        {
            return;
        }

        var tail = string.Join(Environment.NewLine, output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(40)
            .Select(l => l.TrimEnd()));
        throw new InvalidOperationException(
            $"docker build failed for {imageName} (exit {process.ExitCode}):{Environment.NewLine}{tail}");
    }

    private static async Task DrainAsync(params Task<string>[] reads)
    {
        try
        {
            await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // The build is already being torn down; whatever it managed to print no longer matters.
        }
    }

    // An OS file handle opened with FileShare.None is released automatically if the process
    // dies, so no stale-lock cleanup is needed. Bounded by the caller's CancellationToken
    // (the fixture timeout).
    private static async Task<FileStream> AcquireImageFileLockAsync(string imageName, CancellationToken ct)
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"agent-tests-image-{SafeImageName(imageName)}.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
    }

    internal static bool IsImageFresh(bool imageExists, DateTimeOffset? lastBuiltAt, DateTimeOffset newestSource) =>
        imageExists && lastBuiltAt.HasValue && newestSource <= lastBuiltAt.Value;

    // The stamp records the source state the image was built *from*, not the wall clock at the
    // end of the build: a file edited while the build was running must still read as stale.
    internal static void RecordImageBuild(string imageName, DateTimeOffset builtAt) =>
        File.WriteAllText(BuildStampPath(imageName), builtAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

    internal static DateTimeOffset? ReadImageBuildStamp(string imageName)
    {
        var path = BuildStampPath(imageName);
        if (!File.Exists(path))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            File.ReadAllText(path),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var stamp)
            ? stamp
            : null;
    }

    internal static string BuildStampPath(string imageName) =>
        Path.Combine(Path.GetTempPath(), $"agent-tests-image-{SafeImageName(imageName)}.stamp");

    private static string SafeImageName(string imageName) =>
        string.Concat(imageName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    private static readonly string[] _buildOutputDirs = ["bin", "obj"];

    internal static DateTimeOffset GetNewestSourceTimestamp(
        string solutionRoot,
        IReadOnlyList<string> watchedDirs,
        string dockerfile)
    {
        // A watched entry is usually a directory, but global.json is a single file the base-sdk
        // image copies in. Without the File.Exists arm it would be silently dropped here, and
        // the E2EImages guard test would pass while edits to it never forced a rebuild.
        var dirTimestamps = watchedDirs
            .Select(d => Path.Combine(solutionRoot, d))
            .SelectMany(p => Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                    .Where(f => !_buildOutputDirs.Any(b =>
                        f.Contains($"{Path.DirectorySeparatorChar}{b}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
                : File.Exists(p) ? [p] : [])
            .Select(f => new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero));

        var dockerfilePath = Path.Combine(solutionRoot, dockerfile);
        var dockerfileTimestamp = File.Exists(dockerfilePath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(dockerfilePath), TimeSpan.Zero)
            : DateTimeOffset.MinValue;

        return dirTimestamps
            .Append(dockerfileTimestamp)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Max();
    }

    // Only existence is asked of docker here. Freshness deliberately does not come from
    // `--format={{.Created}}`: BuildKit reuses the cached image config when a rebuild produces
    // identical content, so `.Created` stays pinned to when those layers were first produced and
    // no rebuild can move it forward. Pair that with a source tree whose mtimes advance without
    // its content changing — `dotnet format` in the pre-commit hook rewrites files whole — and
    // the comparison became permanently unsatisfiable, rebuilding all six images every run.
    private static async Task<bool> DockerImageExistsAsync(string imageName, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("docker", $"image inspect {imageName} --format={{{{.Id}}}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}