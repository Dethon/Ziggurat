using Docker.DotNet;
using Docker.DotNet.Models;
using Domain.DTOs.Voice;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using McpChannelVoice.Services.Tse;

namespace Tests.Integration.Fixtures;

// Spins the real tse-extractor sidecar (DockerCompose/tse-extractor) as a testcontainer instead of
// leaning on a manually-run local sidecar. The WeSep BSRNN+ECAPA checkpoint is a ~280 MB runtime
// download the compose stack provisions into DockerCompose/volumes/tse-models; we bind-mount that
// (read-only) rather than re-download it inside a test, so the checkpoint being present is a hard
// precondition -- and so is the exported ONNX core artifact: the RO mount means load_or_export
// cannot create it in here (it would silently fall back to eager for the whole container
// lifetime), so it too must come from booting the compose service once. When Docker, the image,
// or the provisioned volume is unavailable the fixture skips (never hard-fails) by exposing a
// SkipReason the tests gate on -- matching the External-category convention of the
// Camoufox/Playwright container tests.
public class TseExtractorFixture : IAsyncLifetime
{
    private const string Image = "tse-extractor:latest";
    private const int TsePort = 9098;

    // A synthetic enrollment is seeded for this speaker so the extract round-trip test is
    // deterministic instead of skipping whenever no real voice is enrolled on the box.
    public const string EnrolledSpeaker = "test-speaker";

    private IContainer? _container;
    private string? _voicesDir;

    public string? SkipReason { get; private set; }
    public string BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var modelDir = LocateCheckpointDir();
        if (modelDir is null)
        {
            SkipReason = "tse-extractor checkpoint (or its exported ONNX core) not provisioned at "
                + "DockerCompose/volumes/tse-models/wesep-english (build/run the tse-extractor "
                + "compose service once to download the checkpoint and export the core)";
            return;
        }

        _voicesDir = Path.Combine(Path.GetTempPath(), $"tse-voices-{Guid.NewGuid():N}");
        var speakerDir = Path.Combine(_voicesDir, EnrolledSpeaker);
        Directory.CreateDirectory(speakerDir);
        await File.WriteAllBytesAsync(Path.Combine(speakerDir, "enroll-01.wav"), SyntheticEnrollmentWav());

        try
        {
            var containerBuilder = await ResolveImageAsync();

            _container = containerBuilder
                .WithPortBinding(TsePort, true)
                .WithBindMount(modelDir, "/models", AccessMode.ReadOnly)
                .WithBindMount(_voicesDir, "/voices", AccessMode.ReadOnly)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(TsePort).ForPath("/health")))
                .Build();

            // Cold start loads the 282 MB checkpoint, well past the default port-wait budget, so
            // give StartAsync a generous ceiling. (No export happens in here: /models is RO and
            // the ONNX artifact is a provisioning precondition, checked in LocateCheckpointDir.)
            using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await _container.StartAsync(startCts.Token);

            BaseUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(TsePort)}";
        }
        catch (Exception ex)
        {
            SkipReason = $"tse-extractor container could not be started: {ex.Message}";
        }
    }

    public HttpClient CreateHttpClient() => new() { BaseAddress = new Uri(BaseUrl) };

    // Reuse tse-extractor:latest when the compose stack has already built it; otherwise build it
    // from the Dockerfile so the fixture is self-provisioning. Rebuilding an existing image would
    // only re-stamp a fresh testcontainers session-id label and orphan the multi-GB layers.
    private static async Task<ContainerBuilder> ResolveImageAsync()
    {
        if (await ImageExistsAsync())
        {
            return TestContainers.Container(Image);
        }

        var dockerfileDir = Path.Combine(
            E2E.Fixtures.TestHelpers.FindSolutionRoot(), "DockerCompose", "tse-extractor");
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(dockerfileDir)
            .WithDockerfile("Dockerfile")
            .WithName(Image)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();

        // The tse image installs torch + the wesep tree and compiles hdbscan -- minutes, not seconds.
        using var buildCts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await image.CreateAsync(buildCts.Token);
        return TestContainers.Container(image);
    }

    private static async Task<bool> ImageExistsAsync()
    {
        using var client = new DockerClientBuilder().Build();
        var images = await client.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [Image] = true }
            }
        });
        return images.Count > 0;
    }

    private static string? LocateCheckpointDir()
    {
        var modelDir = Path.Combine(
            E2E.Fixtures.TestHelpers.FindSolutionRoot(), "DockerCompose", "volumes", "tse-models");
        var checkpoint = Path.Combine(modelDir, "wesep-english");
        var provisioned = File.Exists(Path.Combine(checkpoint, "avg_model.pt"))
            && File.Exists(Path.Combine(checkpoint, "config.yaml"))
            && Directory.GetFiles(checkpoint, "bsrnn_core_v*.onnx").Length > 0;
        return provisioned ? modelDir : null;
    }

    private static byte[] SyntheticEnrollmentWav(double seconds = 3.0)
    {
        var samples = (int)(16000 * seconds);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(8000 * Math.Sin(2 * Math.PI * 180 * i / 16000.0));
            BitConverter.GetBytes(value).CopyTo(pcm, i * 2);
        }
        return WavCodec.Encode([new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard }]);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        if (_voicesDir is not null && Directory.Exists(_voicesDir))
        {
            try
            { Directory.Delete(_voicesDir, recursive: true); }
            catch { /* best effort — container teardown may briefly hold handles */ }
        }
    }
}