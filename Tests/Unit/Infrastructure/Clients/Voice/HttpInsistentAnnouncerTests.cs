using System.Net;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class HttpInsistentAnnouncerTests
{
    [Fact]
    public async Task StartAsync_PostsAnnounceWithTokenAndReturnsResponse()
    {
        var handler = new VoiceHubStubHandler(_ => VoiceHubStubHandler.Json(
            HttpStatusCode.Accepted, new AnnounceResponse { AnnouncementId = "a1", Satellites = [] }));
        var sut = new HttpInsistentAnnouncer(VoiceHubStubHandler.Factory(handler), "secret");

        var result = await sut.StartAsync(
            new AnnounceRequest { Target = new() { Room = "Kitchen" }, Text = "pasta is ready" }, CancellationToken.None);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/voice/announce");
        handler.LastRequest.Headers.GetValues("X-Announce-Token").ShouldContain("secret");
        handler.LastBody!.ShouldContain("pasta is ready");
        result.AnnouncementId.ShouldBe("a1");
    }
}