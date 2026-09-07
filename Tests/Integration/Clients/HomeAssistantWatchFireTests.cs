using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The whole bridge against a real Home Assistant: a prompt watch written through the real mount
// becomes a real automation, the sensor is pushed across the threshold through the REST states
// endpoint, and the home posts the rendered prompt and the firing facts to the callback the
// fixture owns. Crossing semantics are the home's own: a second change in the same direction does
// not fire, a change back and across does.
public class HomeAssistantWatchFireTests(HomeAssistantFixture fixture) : IClassFixture<HomeAssistantFixture>
{
    private const string Sensor = "sensor.itest_glucose";

    [Fact]
    public async Task AStateChangeAcrossTheThreshold_FiresTheWatch_AndTheCallbackReceivesThePrompt()
    {
        var client = fixture.CreateClient();
        var watchId = $"itest-sugar-{Guid.NewGuid():N}"[..24];
        using var http = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
        await SetStateAsync(http, "120");

        var fs = new HaFileSystem(new HaCatalogProvider(() => client), () => client,
            // The user is a Telegram chat id, as a person with no username is known: the home
            // renders the watch's literals through a `variables` step, and this pins that a value
            // which looks like a number still reaches the callback as the string the mount wrote.
            caller: () => new ConversationContext("jonas", "conv-1", "123456789", new ReplyTarget("telegram", "conv-1")));
        var created = await fs.CreateAsync($"watches/{watchId}/watch.json", $$$"""
            {"name": "Laura's sugar above 180",
             "triggers": [{"trigger": "numeric_state", "entity_id": "{{{Sensor}}}", "above": 180}],
             "effects": [{"kind": "prompt", "prompt": "Her sugar is {{ trigger.to_state.state }}: look into it."}]}
            """, false, true, CancellationToken.None);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        try
        {
            // The config write reloads the automation; it is live once its entity is on.
            await Eventually.Until(
                async () => (await client.ListAutomationsAsync()).Any(a => a.ConfigId == HaWatchAutomation.AutomationId(watchId) && a.IsOn),
                "the watch's automation to be loaded and on");
            var firesBefore = fixture.Fires.Count;

            await SetStateAsync(http, "190");
            await Eventually.Until(() => fixture.Fires.Count > firesBefore, "the crossing to reach the callback");

            var fire = fixture.Fires[^1];
            fire.Token.ShouldBe(HomeAssistantSeed.WatchCallbackToken);
            fire.Payload["watchId"]!.GetValue<string>().ShouldBe(watchId);
            fire.Payload["name"]!.GetValue<string>().ShouldBe("Laura's sugar above 180");
            fire.Payload["agentId"]!.GetValue<string>().ShouldBe("jonas");
            fire.Payload["deliverTo"]!.ToJsonString().ShouldBe("""["telegram"]""");
            fire.Payload["userId"]!.GetValue<string>().ShouldBe("123456789");
            fire.Payload["prompt"]!.GetValue<string>().ShouldBe("Her sugar is 190: look into it.");
            fire.Payload["entityId"]!.GetValue<string>().ShouldBe(Sensor);
            fire.Payload["fromState"]!.GetValue<string>().ShouldBe("120");
            fire.Payload["toState"]!.GetValue<string>().ShouldBe("190");
            fire.Payload["firedAt"]!.GetValue<string>().ShouldStartWith("20");

            // Further above is not another crossing.
            await SetStateAsync(http, "195");
            await Eventually.Settle();
            fixture.Fires.Count.ShouldBe(firesBefore + 1);

            // Back under and across again is.
            await SetStateAsync(http, "150");
            await SetStateAsync(http, "185");
            await Eventually.Until(() => fixture.Fires.Count > firesBefore + 1, "the second crossing to reach the callback");
            fixture.Fires[^1].Payload["toState"]!.GetValue<string>().ShouldBe("185");

            var status = (await fs.ReadAsync($"watches/{watchId}/status.json", null, null, CancellationToken.None))
                .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;
            status.ShouldContain("\"lastTriggeredAt\": \"20");
        }
        finally
        {
            await fs.DeleteAsync($"watches/{watchId}", CancellationToken.None);
        }
    }

    // A one-shot turns itself off only on a fire the stack took: the callback answering 503 (nobody
    // connected) leaves it armed for the next crossing, and the fire it does take spends it.
    [Fact]
    public async Task AOneShotWhoseFireTheCallbackRefuses_StaysArmed_AndIsSpentByAFireItTakes()
    {
        var client = fixture.CreateClient();
        var watchId = $"itest-once-{Guid.NewGuid():N}"[..24];
        var automationId = HaWatchAutomation.AutomationId(watchId);
        using var http = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
        await SetStateAsync(http, "120");

        var fs = new HaFileSystem(new HaCatalogProvider(() => client), () => client,
            caller: () => new ConversationContext("jonas", "conv-1", "fran", new ReplyTarget("telegram", "conv-1")));
        var created = await fs.CreateAsync($"watches/{watchId}/watch.json", $$$"""
            {"name": "Laura's sugar above 180, once",
             "triggers": [{"trigger": "numeric_state", "entity_id": "{{{Sensor}}}", "above": 180}],
             "effects": [{"kind": "prompt", "prompt": "Look into it."}],
             "once": true}
            """, false, true, CancellationToken.None);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        try
        {
            await Eventually.Until(
                async () => (await client.ListAutomationsAsync()).Any(a => a.ConfigId == automationId && a.IsOn),
                "the watch's automation to be loaded and on");

            fixture.CallbackStatus = 503;
            var firesBefore = fixture.Fires.Count;
            await SetStateAsync(http, "190");
            await Eventually.Until(() => fixture.Fires.Count > firesBefore, "the refused crossing to reach the callback");
            await Eventually.Settle();

            (await client.ListAutomationsAsync()).Single(a => a.ConfigId == automationId).IsOn.ShouldBeTrue();
            (await StatusAsync(fs, watchId)).ShouldContain("\"spent\": false");

            fixture.CallbackStatus = 202;
            await SetStateAsync(http, "150");
            await SetStateAsync(http, "185");
            await Eventually.Until(() => fixture.Fires.Count > firesBefore + 1, "the taken crossing to reach the callback");
            await Eventually.Until(
                async () => !(await client.ListAutomationsAsync()).Single(a => a.ConfigId == automationId).IsOn,
                "the spent one-shot to turn itself off");

            (await StatusAsync(fs, watchId)).ShouldContain("\"spent\": true");
        }
        finally
        {
            fixture.CallbackStatus = 202;
            await fs.DeleteAsync($"watches/{watchId}", CancellationToken.None);
        }
    }

    private static async Task<string> StatusAsync(HaFileSystem fs, string watchId) =>
        (await fs.ReadAsync($"watches/{watchId}/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;

    private static async Task SetStateAsync(HttpClient http, string state)
    {
        var response = await http.PostAsJsonAsync($"api/states/{Sensor}", new JsonObject
        {
            ["state"] = state,
            ["attributes"] = new JsonObject { ["unit_of_measurement"] = "mg/dL", ["friendly_name"] = "Glucose" }
        });
        response.EnsureSuccessStatusCode();
    }
}