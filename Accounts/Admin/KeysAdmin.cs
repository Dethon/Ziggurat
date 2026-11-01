using Accounts.Configuration;
using Accounts.Keys;
using Domain.Agents;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;

namespace Accounts.Admin;

// The household's keys, set by its owners: each key is put to its provider before it is saved, a key
// the provider refuses is not saved, and one saved while the provider could not be asked stands as
// unchecked with a warning. Saved through the keys section's own save and rules, as the operator's
// command saves it, so it is sealed and announced the same way. What is answered is each key's state,
// when it was last checked and its last few characters, never the key.
public sealed class KeysAdmin(
    HouseholdConfigStore store,
    HouseholdKeyChecks checks,
    IKeyProbe<OpenRouterKey> openRouter,
    IKeyProbe<IdealistaKeys> idealista,
    IKeyProbe<TelegramBotKey> telegram,
    ILogger<KeysAdmin> logger)
{
    public const string SecretsUnavailableMessage =
        "The household's keys cannot be opened on this deployment just now, so none can be read or saved. The operator can tell you why.";

    // A save the readers were not told of still stands; they read it once what they keep runs out.
    public const string NotToldWarning =
        "Saved, but the household's agents and servers could not be told just now: they use the new key within a minute.";

    public static void Map(RouteGroupBuilder household)
    {
        household.MapGet(AdminApi.Under(AdminProtocol.Household, AdminProtocol.Keys),
            async (AdminCaller caller, KeysAdmin keys, CancellationToken ct) =>
            {
                try
                {
                    return await keys.ViewAsync(caller.HouseholdId, ct) is { } view ? Results.Ok(view) : Results.NotFound();
                }
                catch (HouseholdSecretsUnavailableException)
                {
                    return SecretsUnavailable();
                }
            });

        household.MapPut(AdminApi.Under(AdminProtocol.Household, AdminProtocol.OpenRouterKey),
            (AdminCaller caller, OpenRouterKey? key, HttpContext http, KeysAdmin keys, AdminLog log, CancellationToken ct) =>
                keys.SaveOpenRouterAsync(key, new Saving(caller, http, log), ct));

        household.MapPut(AdminApi.Under(AdminProtocol.Household, AdminProtocol.IdealistaKeys),
            (AdminCaller caller, IdealistaKeys? key, HttpContext http, KeysAdmin keys, AdminLog log, CancellationToken ct) =>
                keys.SaveIdealistaAsync(key, new Saving(caller, http, log), ct));

        household.MapPut(AdminApi.Under(AdminProtocol.Household, AdminProtocol.TelegramBotRoute),
            (string agentId, AdminCaller caller, TelegramBotKey? key, HttpContext http, KeysAdmin keys, AdminLog log,
                CancellationToken ct) => keys.SaveBotAsync(agentId, key, new Saving(caller, http, log), ct));
    }

    private Task<IResult> SaveOpenRouterAsync(OpenRouterKey? key, Saving saving, CancellationToken ct) =>
        SaveAsync(KeySlot.OpenRouter, openRouter, key, saving, ct);

    private Task<IResult> SaveIdealistaAsync(IdealistaKeys? key, Saving saving, CancellationToken ct) =>
        SaveAsync(KeySlot.Idealista, idealista, key, saving, ct);

    // A bot is bound to one of the household's agents, the ones its turns are built from.
    private async Task<IResult> SaveBotAsync(string agentId, TelegramBotKey? key, Saving saving, CancellationToken ct)
    {
        var agents = await store.ReadAsync(saving.Caller.HouseholdId, HouseholdSections.AgentsSchema, ct);
        return agents is not null && HouseholdAgents.Names(agents).Agents.Any(a => a.Id == agentId)
            ? await SaveAsync(KeySlot.TelegramBot(agentId), telegram, key, saving, ct)
            : Results.BadRequest(new AdminRefusal($"The bot was not saved: the household has no agent {agentId} to bind it to.", []));
    }

    // Not the owner's to fix and not the request's fault: the deployment cannot open the household's
    // secrets, which a read meets as a save does.
    private static IResult SecretsUnavailable() =>
        Results.Json(new AdminRefusal(SecretsUnavailableMessage, []), statusCode: StatusCodes.Status503ServiceUnavailable);

    // Who is saving and the request's log entry, carried to the one save every key goes through.
    private sealed record Saving(AdminCaller Caller, HttpContext Http, AdminLog Log);

    // The keys as the owner is shown them, or null for a household that does not exist.
    public async Task<HouseholdKeys?> ViewAsync(string householdId, CancellationToken ct)
    {
        if (await store.HouseholdAsync(householdId, ct) is not { } household
            || await store.ReadAsync(householdId, HouseholdSections.KeysSchema, ct) is not { } keys)
        {
            return null;
        }

        var known = await checks.ForAsync(householdId, ct);
        return new HouseholdKeys(
            household.IsHome,
            Status(KeySlot.OpenRouter, keys, known),
            Status(KeySlot.Idealista, keys, known),
            [
                .. keys.TelegramBots.Keys.Order(StringComparer.Ordinal)
                    .Select(agent => new TelegramBotStatus(agent, Status(KeySlot.TelegramBot(agent), keys, known)!))
            ]);
    }

    // A key OpenRouter refused in a turn: marked rejected at once, then put to OpenRouter again, whose
    // fresh answer stands — a key it takes again works, and one it could not be asked about stays
    // rejected, since a turn just saw it refused. The household's key is the one saved now: a key the
    // owner replaced since the turn began is checked, not the one that failed.
    public async Task<KeyState?> InUseRejectedAsync(string householdId, CancellationToken ct)
    {
        if (await store.ReadAsync(householdId, HouseholdSections.KeysSchema, ct) is not { OpenRouter: { } key })
        {
            return null;
        }

        var slot = KeySlot.OpenRouter;
        await checks.RecordAsync(householdId, slot.Name, slot.Material(key), KeyState.Rejected,
            "OpenRouter refused it during a turn", ct);
        switch (await openRouter.ProbeAsync(key, ct))
        {
            case KeyProbeOutcome.Working:
                await checks.RecordAsync(householdId, slot.Name, slot.Material(key), KeyState.Working, null, ct);
                return KeyState.Working;
            case KeyProbeOutcome.Rejected rejected:
                await checks.RecordAsync(householdId, slot.Name, slot.Material(key), KeyState.Rejected, rejected.Why, ct);
                return KeyState.Rejected;
            default:
                return KeyState.Rejected;
        }
    }

    private async Task<IResult> SaveAsync<TKey>(
        KeySlot<TKey> slot, IKeyProbe<TKey> probe, TKey? key, Saving saving, CancellationToken ct) where TKey : class
    {
        var caller = saving.Caller;
        if (key is null)
        {
            return Results.BadRequest(new AdminRefusal($"The {slot.Label} was not saved: send it as a JSON object.", []));
        }

        try
        {
            if (await store.ReadAsync(caller.HouseholdId, HouseholdSections.KeysSchema, ct) is not { } keys)
            {
                return Results.NotFound();
            }

            // The section's own rules first, so a blank key is refused in their words without asking
            // a provider about it.
            var document = HouseholdSections.KeysSchema.Write(slot.Put(keys, key));
            if (HouseholdSections.KeysSchema.Problems(document) is { Count: > 0 } problems)
            {
                return Results.BadRequest(new AdminRefusal($"The {slot.Label} was not saved.", problems));
            }

            var outcome = await probe.ProbeAsync(key, ct);
            if (outcome is KeyProbeOutcome.Rejected rejected)
            {
                return Results.BadRequest(new AdminRefusal($"The {slot.Label} was not saved: {rejected.Why}.", []));
            }

            return await AdminSections.AnswerAsync(
                await store.SaveAsync(caller.HouseholdId, HouseholdSections.KeysSchema, document, ct),
                $"The {slot.Label} was not saved.",
                async saved =>
                {
                    var unreachable = outcome as KeyProbeOutcome.Unreachable;
                    await checks.RecordAsync(caller.HouseholdId, slot.Name, slot.Material(key),
                        unreachable is null ? KeyState.Working : KeyState.Unchecked, unreachable?.Why, ct);
                    await saving.Log.RecordAsync(saving.Http, caller, $"{(slot.Read(keys) is null ? "Set" : "Replaced")} the {slot.Label}", ct);
                    string?[] warnings =
                    [
                        unreachable is null
                            ? null
                            : $"Saved, but {probe.Provider} could not be asked whether it works just now ({unreachable.Why}): it shows as unchecked.",
                        saved.NotTold is null ? null : NotToldWarning
                    ];
                    var warning = string.Join(" ", warnings.OfType<string>());
                    return Results.Ok(new AdminSaved<HouseholdKeys>(
                        (await ViewAsync(caller.HouseholdId, ct))!, warning.Length == 0 ? null : warning));
                });
        }
        catch (HouseholdSecretsUnavailableException ex)
        {
            logger.LogWarning("A household's keys could not be saved: {Reason}", ex.Message);
            return SecretsUnavailable();
        }
    }

    private static KeyStatus? Status<TKey>(
        KeySlot<TKey> slot, KeysSection keys, IReadOnlyDictionary<string, Store.HouseholdKeyCheck> known)
        where TKey : class
    {
        if (slot.Read(keys) is not { } key)
        {
            return null;
        }

        var shown = slot.Shown(key);
        var last = shown.Length >= AdminProtocol.ShortestKeyShown ? shown[^AdminProtocol.KeyCharactersShown..] : "";
        return known.TryGetValue(slot.Name, out var check)
               && check.Fingerprint == HouseholdKeyChecks.Fingerprint(slot.Material(key))
               && Enum.TryParse<KeyState>(check.State, out var state)
            ? new KeyStatus(state, last, check.CheckedAt, check.Reason)
            : new KeyStatus(KeyState.Unchecked, last, null);
    }
}

// One key of the keys section: where it is read and put, what its check is fingerprinted from (the
// whole of it: Idealista's pair is one key), and which part of it is shown by its last characters
// (never Idealista's secret).
public sealed record KeySlot<TKey>(
    string Name,
    string Label,
    Func<KeysSection, TKey?> Read,
    Func<KeysSection, TKey, KeysSection> Put,
    Func<TKey, string> Material,
    Func<TKey, string> Shown) where TKey : class;

public static class KeySlot
{
    public static readonly KeySlot<OpenRouterKey> OpenRouter = new(
        "openRouter", "OpenRouter key", keys => keys.OpenRouter, (keys, key) => keys with { OpenRouter = key },
        key => key.ApiKey, key => key.ApiKey);

    public static readonly KeySlot<IdealistaKeys> Idealista = new(
        "idealista", "Idealista keys", keys => keys.Idealista, (keys, key) => keys with { Idealista = key },
        key => $"{key.ApiKey}\n{key.Secret}", key => key.ApiKey);

    public static KeySlot<TelegramBotKey> TelegramBot(string agentId) => new(
        $"telegramBots/{agentId}", $"Telegram bot for {agentId}",
        keys => keys.TelegramBots.GetValueOrDefault(agentId),
        (keys, key) => keys with { TelegramBots = new Dictionary<string, TelegramBotKey>(keys.TelegramBots) { [agentId] = key } },
        key => key.Token, key => key.Token);
}