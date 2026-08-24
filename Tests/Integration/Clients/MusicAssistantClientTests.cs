using Domain.Exceptions;
using Domain.Tools.MusicAssistant;
using Infrastructure.Clients.MusicAssistant;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// Runs the real client against a websocket server that speaks Music Assistant's protocol.
public class MusicAssistantClientTests
{
    private static MaUri Show =>
        MaUri.TryParse(FakeMusicAssistantServer.ShowUri, out var uri) ? uri : throw new InvalidOperationException();

    private static MusicAssistantClient Client(FakeMusicAssistantServer server, string? token = null) =>
        new(server.BaseUrl, token ?? FakeMusicAssistantServer.ValidToken);

    [Fact]
    public async Task GetPodcastEpisodesAsync_ReturnsEveryEpisodeWithItsPlayableUri()
    {
        await using var server = await FakeMusicAssistantServer.StartAsync();

        var episodes = await Client(server).GetPodcastEpisodesAsync(Show, CancellationToken.None);

        episodes.Count.ShouldBe(3);
        episodes[2].Name.ShouldStartWith("280. Palantir");
        episodes[2].Uri.ShouldBe("spotify--w2nq2jMe://podcast_episode/4Fk1sWv0xKvJ6teiCpTAJN");
        episodes[2].DurationSeconds.ShouldBe(7276);
    }

    // MA flags a multi-frame response with partial:true on every frame but the last; dropping the
    // continuation would silently truncate a long show's episode list.
    [Fact]
    public async Task GetPodcastEpisodesAsync_PartialFrames_AccumulatesUntilTheFinalFrame()
    {
        await using var server = await FakeMusicAssistantServer.StartAsync();
        server.Chunks = 3;

        var episodes = await Client(server).GetPodcastEpisodesAsync(Show, CancellationToken.None);

        episodes.Count.ShouldBe(3);
        episodes.Select(e => e.Uri).ShouldBeUnique();
    }

    [Fact]
    public async Task GetPodcastEpisodesAsync_UnknownShow_ThrowsWithServerDetails()
    {
        await using var server = await FakeMusicAssistantServer.StartAsync();
        MaUri.TryParse("spotify--w2nq2jMe://podcast/NOPE", out var missing).ShouldBeTrue();

        var ex = await Should.ThrowAsync<MusicAssistantException>(
            () => Client(server).GetPodcastEpisodesAsync(missing, CancellationToken.None));

        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task SearchAsync_ReturnsShowsWithUris()
    {
        await using var server = await FakeMusicAssistantServer.StartAsync();

        var hits = await Client(server).SearchAsync(
            "No es el fin del mundo", "podcast", 5, CancellationToken.None);

        hits.Count.ShouldBe(2);
        hits[0].Name.ShouldBe("No es el fin del mundo");
        hits[0].Uri.ShouldBe(FakeMusicAssistantServer.ShowUri);
    }

    [Fact]
    public async Task GetPodcastEpisodesAsync_AuthenticatesBeforeIssuingTheCommand()
    {
        await using var server = await FakeMusicAssistantServer.StartAsync();

        await Client(server).GetPodcastEpisodesAsync(Show, CancellationToken.None);

        server.AuthCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetPodcastEpisodesAsync_BadToken_ThrowsBeforeTheCommand()
    {
        await using var server = await FakeMusicAssistantServer.StartAsync();

        var ex = await Should.ThrowAsync<MusicAssistantException>(
            () => Client(server, "wrong").GetPodcastEpisodesAsync(Show, CancellationToken.None));

        ex.Message.ShouldContain("token", Case.Insensitive);
    }

    [Fact]
    public async Task GetPodcastEpisodesAsync_ServerUnreachable_ThrowsMusicAssistantException()
    {
        var client = new MusicAssistantClient($"http://127.0.0.1:{TestPort.GetAvailable()}", "t");

        await Should.ThrowAsync<MusicAssistantException>(
            () => client.GetPodcastEpisodesAsync(Show, CancellationToken.None));
    }

    // The position a relative seek is computed from. Home Assistant's media_position is refreshed
    // only by a state transition, so this queue read is the only current number available; the
    // field names and the unix-seconds stamp are the real server's, verified against it.
    [Fact]
    public async Task GetQueuePositionAsync_ReturnsTheQueuesLiveElapsedTime()
    {
        await using var server = await FakeMusicAssistantServer.StartAsync();

        var position = await Client(server).GetQueuePositionAsync(
            FakeMusicAssistantServer.QueueId, CancellationToken.None);

        position.ShouldNotBeNull();
        position.ElapsedTime.ShouldBe(FakeMusicAssistantServer.QueueElapsedTime);
        position.State.ShouldBe("playing");
        position.LastUpdated.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1787605526343));
    }

    [Fact]
    public async Task GetQueuePositionAsync_UnknownQueue_ReturnsNull()
    {
        await using var server = await FakeMusicAssistantServer.StartAsync();

        var position = await Client(server).GetQueuePositionAsync("ma_nowhere", CancellationToken.None);

        position.ShouldBeNull();
    }
}