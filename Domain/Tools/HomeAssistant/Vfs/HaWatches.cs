using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

// The watches as the home holds them. Nothing is stored here: every read lists the automations and
// projects the prefixed ones back into files, every write renders the file into an automation and
// hands it to the home. Func<IHomeAssistantClient>, as the catalog provider takes it, so the
// transient client is not pinned by a singleton.
public sealed class HaWatches(Func<IHomeAssistantClient> clientFactory, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<HaWatch>> ListAsync(CancellationToken ct)
    {
        var client = clientFactory();
        var states = (await client.ListAutomationsAsync(ct))
            .Where(a => HaWatchAutomation.IsWatch(a.ConfigId))
            .ToList();
        var configs = await Task.WhenAll(states.Select(s => client.GetAutomationConfigAsync(s.ConfigId!, ct)));

        return states.Zip(configs)
            .Where(pair => pair.Second is not null)
            .Select(pair => HaWatchAutomation.Project(pair.First.ConfigId!, pair.Second!, pair.First))
            .OfType<HaWatch>()
            .OrderBy(w => w.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<HaWatch?> GetAsync(string watchId, CancellationToken ct)
    {
        var client = clientFactory();
        var id = HaWatchAutomation.AutomationId(watchId);
        var config = await client.GetAutomationConfigAsync(id, ct);
        if (config is null)
        {
            return null;
        }

        var state = (await client.ListAutomationsAsync(ct)).FirstOrDefault(a => a.ConfigId == id);
        return HaWatchAutomation.Project(id, config, state);
    }

    // Create or replace, under the same id, so a change never leaves a second watch. The creator
    // and the creation instant survive a replacement: the agent that created a watch is the one
    // that runs its prompts, whoever edits it later.
    public async Task<HaWatch> WriteAsync(string watchId, HaWatchSpec spec, string agentId, HaWatch? existing, CancellationToken ct)
    {
        var client = clientFactory();
        var meta = new HaWatchMetadata(
            existing?.Meta.AgentId ?? agentId,
            spec.Effects,
            spec.Once,
            spec.DeliverTo,
            spec.UserId,
            existing?.Meta.CreatedAt ?? _time.GetUtcNow());
        var id = HaWatchAutomation.AutomationId(watchId);

        await client.UpsertAutomationConfigAsync(id, HaWatchAutomation.Render(watchId, spec, meta), ct);

        // The config write reloads the automation, and its on/off state is the entity's, not the
        // config's: a pause or a resume is a service call on the entity the reload produced. A
        // brand-new entity can trail the write by a moment, so it is asked for a few times before a
        // watch created paused would be left on.
        var state = await ReloadedStateAsync(client, id, ct);
        if (state is not null && state.IsOn != spec.Enabled)
        {
            await client.CallServiceAsync("automation", spec.Enabled ? "turn_on" : "turn_off", state.EntityId, null, ct);
            state = state with { IsOn = spec.Enabled };
        }

        return new HaWatch(watchId, spec with { Enabled = state?.IsOn ?? spec.Enabled }, meta, state);
    }

    private async Task<HaAutomationState?> ReloadedStateAsync(IHomeAssistantClient client, string id, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var state = (await client.ListAutomationsAsync(ct)).FirstOrDefault(a => a.ConfigId == id);
            if (state is not null || attempt == 4)
            {
                return state;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), _time, ct);
        }
    }

    public Task DeleteAsync(string watchId, CancellationToken ct) =>
        clientFactory().DeleteAutomationConfigAsync(HaWatchAutomation.AutomationId(watchId), ct);

    // The read-only status beside the file: what the automation knows and the file does not.
    public static string RenderStatus(HaWatch watch) => new JsonObject
    {
        ["createdAt"] = watch.Meta.CreatedAt.ToString("o"),
        ["lastTriggeredAt"] = watch.State?.LastTriggered?.ToString("o"),
        ["automationEntity"] = watch.State?.EntityId,
        ["enabled"] = watch.Enabled,
        ["spent"] = watch.Spent
    }.ToJsonString(_indented);

    private static readonly System.Text.Json.JsonSerializerOptions _indented = new() { WriteIndented = true };
}