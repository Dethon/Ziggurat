using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.DTOs.Voice;

namespace Tests.Eval.Fixtures;

// The voice hub, faked at its HTTP handler — the outermost external, exactly where ADR-0030 puts
// a fake. The timers server above it is the real one, so the tool schemas, the mount and the
// prompt a scenario runs against are the ones a deployment serves.
public sealed class FakeVoiceHub : HttpMessageHandler
{
    private readonly IReadOnlyList<SatelliteDescriptor> _roster;

    public FakeVoiceHub(params SatelliteDescriptor[] roster) => _roster = roster;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";

        if (path.EndsWith("api/voice/satellites"))
        {
            return Json(_roster);
        }

        if (path.EndsWith("api/voice/satellites/resolve"))
        {
            var target = await request.Content!.ReadFromJsonAsync<AnnounceTarget>(cancellationToken);
            return Json(Resolve(target));
        }

        // Answered but not recorded: with a clock that never advances nothing fires, and a
        // counter no scenario reads would be a fixture pretending to be an assertion.
        if (path.EndsWith("api/voice/announce"))
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        if (path.EndsWith("api/voice/dismiss"))
        {
            return Json(Array.Empty<string>());
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // The hub's own rule, as far as a timer's creation depends on it: a room matches by name, an
    // id matches exactly, and `all` is everybody. Room matching is case-insensitive because the
    // room a voice turn arrives with is whatever the satellite was configured with.
    private IReadOnlyList<string> Resolve(AnnounceTarget? target) =>
        target switch
        {
            null => [],
            { All: true } => [.. _roster.Select(s => s.Id)],
            { SatelliteIds: { Count: > 0 } ids } => [.. _roster.Select(s => s.Id).Where(ids.Contains)],
            { SatelliteId: { } id } => [.. _roster.Select(s => s.Id).Where(known => known == id)],
            { Room: { } room } =>
            [
                .. _roster
                    .Where(s => string.Equals(s.Room, room, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Id)
            ],
            _ => []
        };

    private static HttpResponseMessage Json<T>(T body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: JsonSerializerOptions.Web) };
}