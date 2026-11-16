using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Unit;

// Whether a test's Lemonade may be handed the NVIDIA card, read off what `docker info` says its
// runtimes are, as scripts/smoke-stack.sh reads it.
public class NvidiaGpuTests
{
    [Fact]
    public void ARuntimeListNamingNvidia_HasTheCard() =>
        NvidiaGpu.InRuntimes("""{"io.containerd.runc.v2":{"path":"runc"},"nvidia":{"path":"nvidia-container-runtime"},"runc":{"path":"runc"}}""")
            .ShouldBeTrue();

    [Theory]
    [InlineData("""{"io.containerd.runc.v2":{"path":"runc"},"runc":{"path":"runc"}}""")]
    [InlineData("")]
    public void ARuntimeListWithoutIt_DoesNot(string runtimes) => NvidiaGpu.InRuntimes(runtimes).ShouldBeFalse();
}