using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Unit;

public class ClipCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clip-cache-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AClipMadeOnce_IsReadBackOnTheNextRun_WithoutBeingMadeAgain()
    {
        var made = 0;
        Task make(string path)
        {
            made++;
            return File.WriteAllBytesAsync(path, [1, 2, 3]);
        }

        var first = await ClipCache.GetAsync(_root, "key", "clip.mp4", make);
        var second = await ClipCache.GetAsync(_root, "key", "clip.mp4", make);

        made.ShouldBe(1);
        second.ShouldBe(first);
    }

    [Fact]
    public async Task AnotherRecipe_IsAnotherClip()
    {
        await ClipCache.GetAsync(_root, "one", "clip.mp4", path => File.WriteAllBytesAsync(path, [1]));
        var other = await ClipCache.GetAsync(_root, "two", "clip.mp4", path => File.WriteAllBytesAsync(path, [2]));

        other.ShouldBe(new byte[] { 2 });
    }

    // The maker writes where the clip's own name ends the path, since ffmpeg picks the container by the
    // extension; and a maker that fails leaves nothing a later run would take for the clip.
    [Fact]
    public async Task AFailedMaking_LeavesNoClip_AndTheMakerSeesTheClipsExtension()
    {
        string? seen = null;
        await Should.ThrowAsync<InvalidOperationException>(() => ClipCache.GetAsync(_root, "key", "clip.webm", async path =>
        {
            seen = path;
            await File.WriteAllBytesAsync(path, [9]);
            throw new InvalidOperationException("ffmpeg failed");
        }));

        Path.GetExtension(seen).ShouldBe(".webm");
        var made = 0;
        await ClipCache.GetAsync(_root, "key", "clip.webm", path =>
        {
            made++;
            return File.WriteAllBytesAsync(path, [1]);
        });
        made.ShouldBe(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}