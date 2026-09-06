using System.Net;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Infrastructure.Clients.Voice;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class HttpSatelliteCatalogTests
{
    [Fact]
    public async Task GetAllAsync_FetchesRosterFromHub()
    {
        var handler = new VoiceHubStubHandler(_ => VoiceHubStubHandler.Json(
            HttpStatusCode.OK, new List<SatelliteDescriptor> { new("kitchen-01", "Kitchen") }));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Factory(handler), "secret");

        var roster = await sut.GetAllAsync(CancellationToken.None);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/voice/satellites");
        handler.LastRequest.Headers.GetValues("X-Announce-Token").ShouldContain("secret");
        roster.ShouldContain(s => s.Id == "kitchen-01" && s.Room == "Kitchen");
    }

    [Fact]
    public async Task GetAllAsync_RefetchesTheRosterOnEveryCall()
    {
        // The roster only changes when the hub restarts with new config — exactly the moment a
        // process-lifetime cache would go stale and wrongly reject the new satellite at create
        // time (or list a dead one in the error roster). Creates are rare: always ask the hub.
        var calls = 0;
        var handler = new VoiceHubStubHandler(_ => VoiceHubStubHandler.Json(
            HttpStatusCode.OK, new List<SatelliteDescriptor> { new($"sat-{++calls}", "Kitchen") }));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Factory(handler), "secret");

        (await sut.GetAllAsync(CancellationToken.None)).ShouldContain(s => s.Id == "sat-1");
        (await sut.GetAllAsync(CancellationToken.None)).ShouldContain(s => s.Id == "sat-2");
    }

    [Fact]
    public async Task GetAllAsync_ConnectionFailure_ThrowsVoiceHubUnavailable()
    {
        var handler = new VoiceHubStubHandler(_ => throw new HttpRequestException("connection refused"));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Factory(handler), "secret");

        await Should.ThrowAsync<VoiceHubUnavailableException>(() => sut.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetAllAsync_RequestTimeout_ThrowsVoiceHubUnavailable()
    {
        // The HttpClient request timeout surfaces as TaskCanceledException with the caller's token
        // still live — hub sickness, not caller cancellation, so it maps to unavailable too.
        var handler = new VoiceHubStubHandler(_ => throw new TaskCanceledException("timed out"));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Factory(handler), "secret");

        await Should.ThrowAsync<VoiceHubUnavailableException>(() => sut.GetAllAsync(CancellationToken.None));
    }

    // The hub answering with an error is not the hub being unreachable — retrying will not fix a
    // token mismatch — but it is still the hub's answer, typed so a mount can say so instead of
    // leaking a raw HTTP exception.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetAllAsync_ErrorStatus_ThrowsVoiceHubRejected_CarryingTheStatus(HttpStatusCode status)
    {
        var handler = new VoiceHubStubHandler(_ => new HttpResponseMessage(status));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Factory(handler), "secret");

        var ex = await Should.ThrowAsync<VoiceHubRejectedException>(() => sut.GetAllAsync(CancellationToken.None));

        ex.StatusCode.ShouldBe((int)status);
        ex.Message.ShouldContain(((int)status).ToString());
    }

    [Fact]
    public async Task ResolveAsync_ErrorStatus_ThrowsVoiceHubRejected()
    {
        var handler = new VoiceHubStubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Factory(handler), "secret");

        var ex = await Should.ThrowAsync<VoiceHubRejectedException>(() => sut.ResolveAsync(new AnnounceTarget { Room = "Kitchen" }, CancellationToken.None));

        ex.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task ResolveAsync_ForwardsTargetToHubResolveEndpoint()
    {
        // Resolution is never done locally off the roster — a DisplayLocation-form room must go to the
        // hub, whose registry dual-keys Room and DisplayLocation, or it would resolve to nothing here.
        var handler = new VoiceHubStubHandler(_ => VoiceHubStubHandler.Json(
            HttpStatusCode.OK, new List<string> { "kitchen-01" }));
        var sut = new HttpSatelliteCatalog(VoiceHubStubHandler.Factory(handler), "secret");

        var ids = await sut.ResolveAsync(new AnnounceTarget { Room = "Kitchen (Madrid, Spain)" }, CancellationToken.None);

        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/voice/satellites/resolve");
        handler.LastBody!.ShouldContain("Madrid");
        ids.ShouldBe(["kitchen-01"]);
    }
}