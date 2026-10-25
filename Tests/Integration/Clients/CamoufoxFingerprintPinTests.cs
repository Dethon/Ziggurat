using Docker.DotNet;
using Domain.Contracts;
using Domain.Judgments;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Integration.Clients;

// The Camoufox service presents one device across restarts: the fingerprint is drawn on the
// first launch over an empty volume, saved there, and handed to every launch after. A site that
// sees a logged-in session from a new machine after each deploy challenges it or drops it.
public class CamoufoxFingerprintPinTests
{
    private const string Image = "camoufox:latest";
    private const string AnchorUrl = "https://fingerprint-anchor.test/";

    // What a site reads to tell one machine from another, fonts included: each candidate is
    // measured against the generic fallback, and a font the device claims renders differently.
    private const string ReadDeviceScript =
        """
        () => {
            const candidates = ['Arial', 'Calibri', 'Cambria', 'Consolas', 'Courier New', 'Georgia',
                'Helvetica', 'Segoe UI', 'Tahoma', 'Times New Roman', 'Trebuchet MS', 'Verdana',
                'Ubuntu', 'DejaVu Sans', 'Liberation Sans', 'Menlo', 'Monaco', 'SF Pro Text'];
            const span = document.createElement('span');
            span.style.fontSize = '72px';
            span.textContent = 'mmmmmmmmmmlli1WQ';
            document.body.appendChild(span);
            const width = family => { span.style.fontFamily = family; return span.offsetWidth; };
            const fallback = width('monospace');
            const fonts = candidates.filter(f => width(`'${f}', monospace`) !== fallback);
            span.remove();
            return [navigator.userAgent, navigator.platform, screen.width + 'x' + screen.height,
                String(navigator.hardwareConcurrency), fonts.join(',')].join('|');
        }
        """;

    private static async Task<bool> ImageExistsAsync()
    {
        try
        {
            using var client = new DockerClientBuilder().Build();
            await client.Images.InspectImageAsync(Image);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> LaunchAndReadDeviceAsync(string volume)
    {
        await using var container = TestContainers.Container(Image, "camoufox-fingerprint")
            .WithPortBinding(9377, true)
            .WithVolumeMount(volume, "/data")
            .WithCreateParameterModifier(p => (p.HostConfig ??= new()).ShmSize = 512L * 1024 * 1024)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(9377).ForPath("/json")))
            .Build();
        using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await container.StartAsync(startCts.Token);

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: $"ws://{container.Hostname}:{container.GetMappedPublicPort(9377)}/browser");
        await browser.RouteOnContextAsync(AnchorUrl, route => route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<!DOCTYPE html><html><body><p>anchor</p></body></html>"
        }));
        var browse = await browser.NavigateAsync(
            new BrowseRequest("fingerprint", AnchorUrl) { Caller = JudgmentCaller.None });
        browse.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
        return await browser.EvaluateOnSessionAsync<string>("fingerprint", ReadDeviceScript);
    }

    private static async Task<bool> FingerprintSavedAsync(string volume)
    {
        await using var probe = TestContainers.Container(Image, "camoufox-fingerprint")
            .WithVolumeMount(volume, "/data")
            .WithEntrypoint("sh", "-c", "test -s /data/fingerprint.json")
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();
        await probe.StartAsync();
        return await probe.GetExitCodeAsync() == 0;
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task TwoLaunchesOverOneVolume_PresentTheSameDevice_AndAFreshVolumeDrawsAnother()
    {
        Skip.IfNot(await ImageExistsAsync(), $"{Image} is not built.");

        var volume = $"camoufox-fingerprint-{Guid.NewGuid():N}";
        var other = $"camoufox-fingerprint-{Guid.NewGuid():N}";
        try
        {
            var first = await LaunchAndReadDeviceAsync(volume);
            (await FingerprintSavedAsync(volume)).ShouldBeTrue("the first launch saves the device it drew");

            var second = await LaunchAndReadDeviceAsync(volume);
            var elsewhere = await LaunchAndReadDeviceAsync(other);

            second.ShouldBe(first);
            elsewhere.ShouldNotBe(first);
        }
        finally
        {
            using var client = new DockerClientBuilder().Build();
            foreach (var name in new[] { volume, other })
            {
                try
                {
                    await client.Volumes.RemoveAsync(name, force: true);
                }
                catch (DockerApiException)
                {
                    // Never created, or already gone.
                }
            }
        }
    }
}