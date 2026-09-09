using System.Net.Http.Json;
using Domain.DTOs.Voice;
using Shouldly;

namespace Tests.Eval.Fixtures;

public class FakeVoiceHubTests
{
    // The scenario says an alarm is ringing, and the tool's honest answer to an empty dismiss is
    // "nothing is ringing" — which sent a model to the calendar to stop the alarm by hand. The
    // hub answers the dismiss with what the scenario declared, once: a second dismiss finds
    // silence, as the real hub would.
    [Fact]
    public async Task ADismiss_AnswersTheRingingAlert_Once()
    {
        var hub = new FakeVoiceHub { Ringing = new DismissedAlert("Sacar la basura", AnnounceKind.Alarm) };
        using var client = new HttpClient(hub) { BaseAddress = new Uri("http://hub.test/") };

        var first = await client.PostAsync("api/voice/dismiss", null);
        var second = await client.PostAsync("api/voice/dismiss", null);

        (await first.Content.ReadFromJsonAsync<List<DismissedAlert>>())
            .ShouldBe([new DismissedAlert("Sacar la basura", AnnounceKind.Alarm)]);
        (await second.Content.ReadFromJsonAsync<List<DismissedAlert>>()).ShouldBeEmpty();
    }
}