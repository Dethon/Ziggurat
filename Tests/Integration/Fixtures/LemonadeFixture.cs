using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Tests.Integration.Fixtures;

// Spins the real lemonade STT/TTS server (DockerCompose/lemonade) as a testcontainer instead of
// leaning on a manually-run compose service. Forces the CPU whisper.cpp tier (STT_BACKEND=cpu) so
// the fixture is portable -- the test pins the transcription *contract*, not GPU throughput, so no
// /dev/dri passthrough is needed. The whisper + Kokoro models are multi-GB runtime downloads
// the compose stack provisions into DockerCompose/volumes/lemonade-*; we bind-mount those (the same
// paths the compose service uses) rather than re-download them inside a test, so their presence is a
// hard precondition. The image itself is not: LemonadeImageFixture builds it when it is missing.
// When Docker or the provisioned cache is unavailable the fixture records a SkipReason the tests
// gate on (never hard-fails) -- the External-category convention shared with the TSE/Camoufox
// container fixtures.
// One Lemonade container for every suite that needs one. Per-class fixtures meant xUnit ran
// three of them at once, each loading a model onto the same iGPU and each holding Lemonade's
// single per-type model slot, which made the suites fail together while passing alone.
[CollectionDefinition(Name)]
public class LemonadeCollection : ICollectionFixture<LemonadeFixture>
{
    public const string Name = "Lemonade";
}

public class LemonadeFixture : IAsyncLifetime
{
    private const int LemonadePort = 13305;

    // The model the entrypoint pre-pulls for recall, and the width its vectors come back at.
    // Its Lemonade registry entry points at Qwen/Qwen3-Embedding-0.6B-GGUF, which is the HF
    // snapshot directory the compose stack provisions into the cache below.
    public const string EmbeddingModel = "Qwen3-Embedding-0.6B-GGUF";
    public const int EmbeddingDimension = 1024;
    private const string EmbeddingModelSnapshot = "models--Qwen--Qwen3-Embedding-0.6B-GGUF";

    private IContainer? _container;

    public string? SkipReason { get; private set; }

    // Includes the /v1 suffix the OpenAiStt/TtsConfig defaults carry, so callers pass it straight
    // into OpenAiSttConfig.BaseUrl / OpenAiTtsConfig.BaseUrl.
    public string BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var volumesDir = LocateProvisionedVolumes();
        if (volumesDir is null)
        {
            SkipReason = "lemonade model cache not provisioned at DockerCompose/volumes/lemonade-* "
                + "(build/run the lemonade compose service once to download the Whisper, Kokoro "
                + "and Qwen3 embedding models)";
            return;
        }

        var imageSkipReason = await LemonadeImageFixture.EnsureAsync();
        if (imageSkipReason is not null)
        {
            SkipReason = imageSkipReason;
            return;
        }

        try
        {
            // Bind-mounted read-write at the same paths the compose service uses: the entrypoint
            // regenerates config.json into the recipe cache on every boot (as it does under compose),
            // and HF hub may touch lock files while loading a cached model. The models themselves are
            // never re-downloaded -- LocateProvisionedVolumes already proved they are present.
            _container = new ContainerBuilder(LemonadeImageFixture.Image)
                .WithPortBinding(LemonadePort, true)
                .WithEnvironment("STT_BACKEND", "cpu")
                .WithBindMount(Path.Combine(volumesDir, "lemonade-hf-cache"),
                    "/opt/lemonade/.cache/huggingface", AccessMode.ReadWrite)
                .WithBindMount(Path.Combine(volumesDir, "lemonade-recipe"),
                    "/opt/lemonade/.cache/lemonade", AccessMode.ReadWrite)
                .WithBindMount(Path.Combine(volumesDir, "lemonade-llama"),
                    "/opt/lemonade/llama", AccessMode.ReadWrite)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(LemonadePort).ForPath("/api/v1/health")))
                .Build();

            // Health flips ready as soon as the server answers; the whisper model only loads on the
            // first decode (paid by the test's own timeout, not here). Give the port-wait a generous
            // ceiling anyway for cold container + recipe-binary bring-up.
            using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await _container.StartAsync(startCts.Token);

            BaseUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(LemonadePort)}/v1";
        }
        catch (Exception ex)
        {
            SkipReason = $"lemonade container could not be started: {ex.Message}";
        }
    }

    // The provisioned cache is a hard precondition: the whisper, Kokoro and embedding HF snapshots
    // (models are read from here) and the installed whisper.cpp recipe binary must all be present,
    // or the container would try to download them inside the test.
    private static string? LocateProvisionedVolumes()
    {
        var volumesDir = Path.Combine(
            E2E.Fixtures.TestHelpers.FindSolutionRoot(), "DockerCompose", "volumes");
        var hub = Path.Combine(volumesDir, "lemonade-hf-cache", "hub");
        var provisioned = Directory.Exists(Path.Combine(hub, "models--ggerganov--whisper.cpp"))
            && Directory.Exists(Path.Combine(hub, "models--mikkoph--kokoro-onnx"))
            && Directory.Exists(Path.Combine(hub, EmbeddingModelSnapshot))
            && Directory.Exists(Path.Combine(volumesDir, "lemonade-recipe", "bin", "whispercpp"));
        return provisioned ? volumesDir : null;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}