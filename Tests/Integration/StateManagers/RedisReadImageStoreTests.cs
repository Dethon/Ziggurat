using Domain.DTOs.FileSystem;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

// Raw bytes, against a real Redis, because that is the part a fake cannot vouch for: an image round
// trips only if the value is written as bytes rather than as a string somewhere along the way, and
// the horizon is a real TTL on a real key rather than a span the code hoped Redis honoured.
public sealed class RedisReadImageStoreTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    private readonly RedisReadImageStore _store = new(fixture.Connection);

    // Bytes that are not valid UTF-8 in either direction, so a round trip that went through a string
    // cannot come back intact.
    private static readonly byte[] _png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE];

    [Fact]
    public async Task AnImage_RoundTripsWithItsMediaTypeAndPath()
    {
        var conversation = Unique();
        await _store.PutAsync(conversation, "call-1", Image(), default);

        var read = await _store.GetAsync(conversation, "call-1", default);

        read.ShouldNotBeNull();
        read.Bytes.ShouldBe(_png);
        read.MediaType.ShouldBe("image/png");
        read.VirtualPath.ShouldBe("/vault/shots/error.png");
    }

    [Fact]
    public async Task TwoImagesInOneConversation_AreKeptApartByTheirCallIds()
    {
        var conversation = Unique();
        await _store.PutAsync(conversation, "call-1", Image("/vault/a.png"), default);
        await _store.PutAsync(conversation, "call-2", Image("/media/b.jpg"), default);

        (await _store.GetAsync(conversation, "call-1", default))!.VirtualPath.ShouldBe("/vault/a.png");
        (await _store.GetAsync(conversation, "call-2", default))!.VirtualPath.ShouldBe("/media/b.jpg");
    }

    [Fact]
    public async Task TheSameCallIdInAnotherConversation_IsADifferentImage()
    {
        await _store.PutAsync(Unique(), "call-1", Image("/vault/a.png"), default);

        (await _store.GetAsync(Unique(), "call-1", default)).ShouldBeNull();
    }

    // The message window is the real bound: the send an image drops out of view deletes it, so the
    // horizon below is only a backstop for a conversation that went quiet.
    [Fact]
    public async Task ADeletedImage_IsGone()
    {
        var conversation = Unique();
        await _store.PutAsync(conversation, "call-1", Image(), default);

        await _store.DeleteAsync(conversation, "call-1", default);

        (await _store.GetAsync(conversation, "call-1", default)).ShouldBeNull();
    }

    [Fact]
    public async Task DeletingAnImageThatWasNeverThere_IsNotAnError()
    {
        await _store.DeleteAsync(Unique(), "call-1", default);
    }

    [Fact]
    public async Task AnImageNobodyWrote_AnswersNothingRatherThanThrowing()
    {
        (await _store.GetAsync(Unique(), "call-missing", default)).ShouldBeNull();
    }

    [Fact]
    public async Task AStoredImage_CarriesTheHorizonOnItsKey()
    {
        var conversation = Unique();
        await _store.PutAsync(conversation, "call-1", Image(), default);

        var ttl = await fixture.Connection.GetDatabase()
            .KeyTimeToLiveAsync(RedisReadImageStore.KeyFor(conversation, "call-1"));

        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeLessThanOrEqualTo(RedisReadImageStore.Horizon);
        ttl.Value.ShouldBeGreaterThan(RedisReadImageStore.Horizon - TimeSpan.FromMinutes(1));
    }

    private static ReadImage Image(string virtualPath = "/vault/shots/error.png") =>
        new() { VirtualPath = virtualPath, MediaType = "image/png", Bytes = _png };

    private static string Unique() => $"conv-{Guid.NewGuid():N}";
}