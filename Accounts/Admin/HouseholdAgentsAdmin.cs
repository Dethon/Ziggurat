using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Settings;
using Domain.Agents;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Domain.Editing;
using Domain.Prompts;
using Microsoft.AspNetCore.Mvc;

namespace Accounts.Admin;

// The household's agents, as its owners manage them over the agents section: an agent's name and
// custom instructions, the versions a change replaced and a restore of one, an agent added from a
// shipped template, one removed. Nothing else about an agent is theirs — model, servers, features and
// sections stay the template's or the seeded definition's — so an edit carries the two fields alone.
//
// Each change edits the section as the household's turns read it (a household that saved none has
// the starting templates, which its first change keeps) and saves it whole through the same save and
// rules as the operator's set-household-config, announced, so the household's next turn is built from it.
public static class HouseholdAgentsAdmin
{
    private static readonly AdminRefusal _noSuchAgent = new("The household has no such agent.", []);

    public static void Map(RouteGroupBuilder household)
    {
        var agents = AdminApi.Under(AdminProtocol.Household, AdminProtocol.Agents);
        var agent = AdminApi.Under(AdminProtocol.Household, AdminProtocol.AgentRoute);

        household.MapGet(agents, async (AdminCaller caller, HouseholdConfigStore store, AccountsSettings settings, CancellationToken ct) =>
            await SectionAsync(store, caller, ct) is { } section ? Results.Ok(Answer(section, settings)) : Results.NotFound());

        household.MapPost(agents, async (
            AdminCaller caller, [FromBody] AgentAddition? addition, HttpContext http, HouseholdConfigStore store, AdminLog log,
            AccountsSettings settings, CancellationToken ct) =>
        {
            if (addition is null || AgentTemplates.Named(addition.Template) is not { } template)
            {
                return Results.BadRequest(new AdminRefusal("No agent was added: name one of the templates this deployment ships.", []));
            }

            if (NameProblem(addition.Name) is { } problem)
            {
                return Results.BadRequest(new AdminRefusal("No agent was added.", [problem]));
            }

            if (await SectionAsync(store, caller, ct) is not { } section)
            {
                return Results.NotFound();
            }

            var id = FreeId(section, template.Id);
            var added = section with
            {
                Agents = [.. section.Agents, new HouseholdAgent { Id = id, Template = template.Id, Name = addition.Name.Trim() }]
            };
            return await SaveAsync(http, caller, store, log, settings, added, $"Added agent {id} from the {template.Id} template", ct);
        });

        household.MapPut(agent, async (
            AdminCaller caller, string agentId, [FromBody] AgentEdit? edit, HttpContext http, HouseholdConfigStore store,
            AdminLog log, AgentVersions versions, AccountsSettings settings, CancellationToken ct) =>
        {
            if (edit is null)
            {
                return Results.BadRequest(new AdminRefusal("The agent was not saved: send its name and custom instructions.", []));
            }

            return await EditAsync(http, caller, agentId, edit, store, log, versions, settings, ct, changed =>
                $"Changed agent {agentId}'s {string.Join(", ", changed)}");
        });

        household.MapDelete(agent, async (
            AdminCaller caller, string agentId, HttpContext http, HouseholdConfigStore store, AdminLog log, AgentVersions versions,
            HouseholdSchedules schedules, AccountsSettings settings, CancellationToken ct) =>
        {
            if (await SectionAsync(store, caller, ct) is not { } section)
            {
                return Results.NotFound();
            }

            if (section.Agents.FirstOrDefault(a => a.Id == agentId) is not { } removing)
            {
                return Results.NotFound(_noSuchAgent);
            }

            // A household always has an agent that answers when nobody named one; and a section with
            // no agent at all is read as the starting templates, which would bring back two agents.
            if (section.DefaultAgent == agentId)
            {
                return Results.BadRequest(new AdminRefusal(
                    $"{Shown(removing).Name} answers by default, so it stays: rename it or change its instructions instead.", []));
            }

            // A space opening on an agent the household no longer has would open on whatever came first.
            if (await store.ReadAsync(caller.HouseholdId, HouseholdSections.SpacesSchema, ct) is { } spaces
                && spaces.Spaces.FirstOrDefault(s => s.DefaultAgent == agentId) is { } space)
            {
                return Results.BadRequest(new AdminRefusal(
                    $"The {space.Name} space opens on {Shown(removing).Name}, so it stays: pick another agent for that space first.", []));
            }

            if (section.Agents.Count == 1)
            {
                return Results.BadRequest(new AdminRefusal("A household keeps at least one agent.", []));
            }

            // A bot bound to it would answer nobody, its id never being another agent's again.
            if (await store.ReadAsync(caller.HouseholdId, HouseholdSections.KeysSchema, ct) is { } keys
                && keys.TelegramBots.ContainsKey(agentId))
            {
                return Results.BadRequest(new AdminRefusal(
                    $"The household's Telegram bot answers as {Shown(removing).Name}, so it stays: bind the bot to another agent first.", []));
            }

            // Its schedules would stay and be refused at every fire, with nobody told.
            switch (await schedules.CountOfAgentAsync(caller.HouseholdId, agentId, ct))
            {
                case null:
                    return Results.Json(new AdminRefusal(
                        $"{Shown(removing).Name} was not removed: whether it still runs schedules could not be checked right now.", []),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                case > 0 and var count:
                    return Results.BadRequest(new AdminRefusal(
                        $"{Shown(removing).Name} runs {count} schedule{(count == 1 ? "" : "s")}, so it stays: ask it to delete them, or to move them to another agent, first.", []));
            }

            // The mark goes with the agent, in the same save: the satellites fall back as if none were marked.
            var result = await SaveAsync(http, caller, store, log, settings,
                section with
                {
                    Agents = [.. section.Agents.Where(a => a.Id != agentId)],
                    VoiceAgent = section.VoiceAgent == agentId ? null : section.VoiceAgent,
                    RemovedIds = [.. section.RemovedIds.Append(agentId).Distinct(StringComparer.Ordinal)]
                }, $"Removed agent {agentId}", ct);
            if (Stood(result))
            {
                await versions.DropAsync(caller.HouseholdId, agentId, ct);
            }

            return result;
        });

        // Which agent answers the household's satellites: one of its agents, or none marked. Saved as the
        // section's own rules hold it, so naming no agent of the household's is refused in their words.
        household.MapPut(AdminApi.Under(AdminProtocol.Household, AdminProtocol.VoiceAgent), async (
            AdminCaller caller, [FromBody] VoiceAgentChoice? choice, HttpContext http, HouseholdConfigStore store, AdminLog log,
            AccountsSettings settings, CancellationToken ct) =>
        {
            if (await SectionAsync(store, caller, ct) is not { } section)
            {
                return Results.NotFound();
            }

            var agentId = string.IsNullOrWhiteSpace(choice?.AgentId) ? null : choice.AgentId;
            if (agentId == section.VoiceAgent)
            {
                return Results.Ok(new AdminSaved<AdminAgents>(Answer(section, settings)));
            }

            return await SaveAsync(http, caller, store, log, settings, section with { VoiceAgent = agentId },
                agentId is null ? "Marked no agent to answer aloud" : $"Marked agent {agentId} to answer aloud", ct);
        });

        household.MapGet(AdminApi.Under(AdminProtocol.Household, AdminProtocol.AgentVersionsRoute), async (
            AdminCaller caller, string agentId, HouseholdConfigStore store, AgentVersions versions, CancellationToken ct) =>
            await SectionAsync(store, caller, ct) is { } section && section.Agents.Any(a => a.Id == agentId)
                ? Results.Ok(await versions.ListAsync(caller.HouseholdId, agentId, ct))
                : Results.NotFound(_noSuchAgent));

        household.MapPost(AdminApi.Under(AdminProtocol.Household, AdminProtocol.RestoreAgentVersionRoute), async (
            AdminCaller caller, string agentId, string versionId, HttpContext http, HouseholdConfigStore store, AdminLog log,
            AgentVersions versions, AccountsSettings settings, CancellationToken ct) =>
            await versions.FindAsync(caller.HouseholdId, agentId, versionId, ct) is { } version
                ? await EditAsync(http, caller, agentId, new AgentEdit(version.Name, version.CustomInstructions), store, log, versions,
                    settings, ct, _ => $"Restored a version of agent {agentId}")
                : Results.NotFound(new AdminRefusal("The agent has no such version.", [])));
    }

    // A change to an agent's name and instructions: held to the admin API's limits, saved, and what it
    // replaced kept as a version, so the change can be undone — a restore's too.
    private static async Task<IResult> EditAsync(
        HttpContext http, AdminCaller caller, string agentId, AgentEdit edit, HouseholdConfigStore store, AdminLog log,
        AgentVersions versions, AccountsSettings settings, CancellationToken ct, Func<IReadOnlyList<string>, string> summary)
    {
        var name = edit.Name?.Trim() ?? "";
        var instructions = Instructions(edit.CustomInstructions);
        var problems = new[]
        {
            NameProblem(name),
            instructions?.Length > settings.Admin.AgentInstructionsLength
                ? $"custom instructions are {instructions.Length} characters; an agent takes at most {settings.Admin.AgentInstructionsLength}"
                : null
        }.OfType<string>().ToList();
        if (problems.Count > 0)
        {
            return Results.BadRequest(new AdminRefusal("The agent was not saved.", problems));
        }

        if (await SectionAsync(store, caller, ct) is not { } section)
        {
            return Results.NotFound();
        }

        if (section.Agents.FirstOrDefault(a => a.Id == agentId) is not { } entry)
        {
            return Results.NotFound(_noSuchAgent);
        }

        var before = Shown(entry);
        // The fields this page changed since it loaded them, over the agent as it is now: another
        // owner's change to the other field stands, and one to the same field is refused.
        if (edit.Loaded is { } loaded)
        {
            var merged = EditMerge.Merge(Fields(loaded.Name?.Trim() ?? "", Instructions(loaded.CustomInstructions)),
                Fields(name, instructions), Fields(before.Name, before.CustomInstructions), new Dictionary<string, string>());
            if (merged.Conflicts.Count > 0)
            {
                return AdminSections.Conflict("The agent was not saved.", merged,
                    path => path[0] == "name" ? "its name" : "its custom instructions");
            }

            name = (string)merged.Value!["name"]!;
            instructions = (string?)merged.Value["instructions"];
        }

        var changed = AdminLog.Changed(("name", before.Name != name), ("custom instructions", before.CustomInstructions != instructions));
        if (changed.Count == 0)
        {
            return Results.Ok(new AdminSaved<AdminAgents>(Answer(section, settings)));
        }

        var edited = section with
        {
            Agents = [.. section.Agents.Select(a => a.Id == agentId ? Edited(a, name, instructions) : a)]
        };
        var result = await SaveAsync(http, caller, store, log, settings, edited, summary(changed), ct);
        if (Stood(result))
        {
            await versions.RecordAsync(caller.HouseholdId, agentId, before, caller.UserId, ct);
        }

        return result;
    }

    private static string? Instructions(string? written) => string.IsNullOrWhiteSpace(written) ? null : written.Trim();

    private static JsonObject Fields(string name, string? instructions) => new() { ["name"] = name, ["instructions"] = instructions };

    private static async Task<IResult> SaveAsync(
        HttpContext http, AdminCaller caller, HouseholdConfigStore store, AdminLog log, AccountsSettings settings,
        AgentsSection section, string summary, CancellationToken ct)
    {
        return await AdminSections.AnswerAsync(
            await store.SaveAsync(caller.HouseholdId, HouseholdSections.AgentsSchema, HouseholdSections.AgentsSchema.Write(section), ct),
            "The agents were not saved.",
            async saved =>
            {
                await log.RecordAsync(http, caller, summary, ct);
                var after = (await SectionAsync(store, caller, ct))!;
                return Results.Ok(new AdminSaved<AdminAgents>(Answer(after, settings), AdminSections.Warning(saved)));
            });
    }

    private static bool Stood(IResult result) => result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK };

    private static async Task<AgentsSection?> SectionAsync(HouseholdConfigStore store, AdminCaller caller, CancellationToken ct) =>
        await store.ReadAsync(caller.HouseholdId, HouseholdSections.AgentsSchema, ct) is { } section
            ? HouseholdAgents.Effective(section)
            : null;

    private static AdminAgents Answer(AgentsSection section, AccountsSettings settings) => new(
        section.DefaultAgent,
        [.. section.Agents.Select(Shown)],
        [.. AgentTemplates.All.Select(t => new AdminAgentTemplate(t.Id, t.Name, t.Description))],
        settings.Admin.AgentInstructionsLength,
        section.VoiceAgent,
        HouseholdAgents.VoiceAgentOf(section) is var aloud && section.Agents.Any(a => a.Id == aloud) ? aloud : null);

    // An entry as an owner sees it, whichever kind: a seeded definition carries its own name and
    // instructions; a template's entry carries them beside the template, whose words and model are its.
    private static AdminAgent Shown(HouseholdAgent entry) => Listed(entry) with { Definition = HouseholdAgents.DefinitionOf(entry) };

    private static AdminAgent Listed(HouseholdAgent entry) => entry switch
    {
        { Definition: { } definition } => new AdminAgent(
            entry.Id, definition.Name, definition.CustomInstructions, null, definition.Description, definition.Model,
            entry.MayAnswerAloud),
        _ => AgentTemplates.Named(entry.Template ?? "") is { } template
            ? new AdminAgent(entry.Id, entry.Name ?? template.Name, entry.CustomInstructions, entry.Template, template.Description,
                template.Model, entry.MayAnswerAloud)
            : new AdminAgent(entry.Id, entry.Name ?? entry.Id, entry.CustomInstructions, entry.Template, null, "", entry.MayAnswerAloud)
    };

    // Where the two fields live for each kind (.claude/rules/household-config.md): a definition's own,
    // or the template entry's beside the template.
    private static HouseholdAgent Edited(HouseholdAgent entry, string name, string? instructions) => entry switch
    {
        { Definition: { } definition } => entry with { Definition = definition with { Name = name, CustomInstructions = instructions } },
        _ => entry with { Name = name, CustomInstructions = instructions }
    };

    // The template's own id while it is free, else the first numbered one that is: an id rides in
    // Redis keys, URLs and a Telegram chat's choice, so it is the slug a template's name already is.
    // A removed agent's id is never free again (AgentsSection.RemovedIds).
    private static string FreeId(AgentsSection section, string template) =>
        Enumerable.Range(1, section.Agents.Count + section.RemovedIds.Count + 1)
            .Select(n => n == 1 ? template : $"{template}-{n}")
            .First(id => section.Agents.All(a => a.Id != id) && !section.RemovedIds.Contains(id));

    private static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > AdminAgent.NameLength
            ? $"an agent's name must be 1–{AdminAgent.NameLength} characters"
            : null;
}