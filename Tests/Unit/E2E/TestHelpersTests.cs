using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.E2E;

public class TestHelpersTests
{
    // Testcontainers' ImageFromDockerfileBuilder posts to the Docker Engine /build endpoint,
    // which serves the legacy builder. That cache is disjoint from the one docker compose and
    // buildx use, and the legacy builder ignores --mount=type=cache, so every E2E rebuild
    // re-resolved NuGet from scratch and could never reuse a compose build. Driving the docker
    // CLI instead puts image builds on BuildKit and back on the shared cache.
    [Fact]
    public void CreateBuildCommand_ForAnyImage_RequestsBuildKit()
    {
        var command = TestHelpers.CreateBuildCommand("/src", "Agent/Dockerfile", "agent:latest");

        command.FileName.ShouldBe("docker");
        command.Environment.ShouldContainKeyAndValue("DOCKER_BUILDKIT", "1");
    }

    [Fact]
    public void CreateBuildCommand_ForAnyImage_TargetsTheDockerfileAndTagWithSolutionRootAsContext()
    {
        var solutionRoot = Path.Combine(Path.DirectorySeparatorChar.ToString(), "src");
        var expectedDockerfile = Path.Combine(solutionRoot, "Agent/Dockerfile");

        var command = TestHelpers.CreateBuildCommand(solutionRoot, "Agent/Dockerfile", "agent:latest");

        command.Arguments.ShouldStartWith("build ");
        command.Arguments.ShouldContain($"--file \"{expectedDockerfile}\"");
        command.Arguments.ShouldContain("--tag \"agent:latest\"");
        command.Arguments.ShouldEndWith($"\"{solutionRoot}\"");
    }

    [Fact]
    public void IsImageFresh_SourceNewerThanTheRecordedBuild_IsStale()
    {
        var builtAt = DateTimeOffset.UtcNow;

        TestHelpers.IsImageFresh(imageExists: true, builtAt, builtAt.AddMinutes(1)).ShouldBeFalse();
    }

    [Fact]
    public void IsImageFresh_ImageWeNeverBuilt_IsStale()
    {
        TestHelpers.IsImageFresh(imageExists: true, null, DateTimeOffset.UtcNow.AddYears(-1)).ShouldBeFalse();
    }

    [Fact]
    public void IsImageFresh_ImageDeletedSinceWeBuiltIt_IsStale()
    {
        var builtAt = DateTimeOffset.UtcNow;

        TestHelpers.IsImageFresh(imageExists: false, builtAt, builtAt.AddMinutes(-1)).ShouldBeFalse();
    }

    // Freshness used to come from `docker image inspect --format={{.Created}}`. BuildKit reuses
    // the cached image config when a rebuild is content-identical, so `.Created` stays pinned to
    // when those layers were first produced. Any source touched after that — `dotnet format`
    // rewriting a file whole is enough, since BuildKit keys COPY on content while the staleness
    // check keys on mtime — made the image permanently stale: rebuilding could never move the
    // timestamp the check was comparing against. Recording our own build stamp is the fix.
    [Fact]
    public void RecordImageBuild_AfterAContentIdenticalRebuild_MarksTheImageFresh()
    {
        var imageName = $"stamp-probe-{Guid.NewGuid():N}:latest";
        var sourceTouchedAt = DateTimeOffset.UtcNow;
        try
        {
            TestHelpers.RecordImageBuild(imageName, sourceTouchedAt.AddSeconds(1));

            var stamp = TestHelpers.ReadImageBuildStamp(imageName);

            stamp.ShouldNotBeNull();
            TestHelpers.IsImageFresh(imageExists: true, stamp, sourceTouchedAt).ShouldBeTrue();
        }
        finally
        {
            File.Delete(TestHelpers.BuildStampPath(imageName));
        }
    }

    [Fact]
    public void ReadImageBuildStamp_ImageThisMachineNeverBuilt_IsNull()
    {
        TestHelpers.ReadImageBuildStamp($"never-built-{Guid.NewGuid():N}:latest").ShouldBeNull();
    }
}