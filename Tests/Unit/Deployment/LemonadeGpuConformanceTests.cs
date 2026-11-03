using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Deployment;

// A local stack runs Lemonade on the machine's NVIDIA card when Docker can hand it one. On the
// development box (WSL, an RTX 4090) the image's Vulkan sees only llvmpipe, so Lemonade's auto
// backend put memory's embeddings on the CPU, where four of them timed out while six chats ran at
// once and what those turns would have remembered was dropped.
public class LemonadeGpuConformanceTests
{
    private static string Root => TestHelpers.FindSolutionRoot();

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path]));

    [Fact]
    public void TheNvidiaOverride_PutsTheEmbeddingsOnCuda_AndGivesLemonadeTheCard()
    {
        var overrideFile = Read("DockerCompose", "docker-compose.override.nvidia.yml");

        overrideFile.ShouldContain("LLAMACPP_BACKEND: cuda");
        overrideFile.ShouldContain("driver: nvidia");
        // Whisper has no CUDA build in Lemonade, and its Vulkan tier would decode on llvmpipe.
        overrideFile.ShouldContain("STT_BACKEND: cpu");
    }

    [Fact]
    public void TheSmokeStack_AppliesTheNvidiaOverride_WhereDockerHasTheRuntime()
    {
        var script = Read("scripts", "smoke-stack.sh");

        script.ShouldContain("docker-compose.override.nvidia.yml");
        script.ShouldContain(".Runtimes");
    }
}