using Accounts.Configuration;
using Domain.DTOs.Household;

namespace Accounts.Internal;

// Each caller's own view of a household's configuration (.claude/rules/household-config.md), one
// route per view, each answered only to the callers it is for (InternalCallers) and for the
// household the caller proved (HouseholdProofs). A secret is unsealed into the view of the server it
// belongs to and left out of every other.
public static class HouseholdConfiguration
{
    public const string Path = "/household-config/";

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost(Path + HouseholdConfigProtocol.AgentView, async (
            HttpContext http, HouseholdConfigQuery query, HouseholdProofs proofs, HouseholdConfigStore store,
            ILogger<HouseholdConfigStore> logger, CancellationToken ct) =>
        {
            if (InternalCallers.Reading(http, HouseholdConfigProtocol.AgentView).Count == 0)
            {
                return InternalCallers.NotYours;
            }

            return await AnswerAsync(await proofs.AgentAsync(query, ct), store, logger, async household =>
                new AgentHouseholdConfig(
                    household.Id,
                    household.IsHome,
                    await SectionAsync(store, household, HouseholdSections.SettingsSchema, ct),
                    await SectionAsync(store, household, HouseholdSections.AgentsSchema, ct),
                    await SectionAsync(store, household, HouseholdSections.FeaturesSchema, ct),
                    await SectionAsync(store, household, HouseholdSections.SpacesSchema, ct),
                    (await SectionAsync(store, household, HouseholdSections.KeysSchema, ct)).OpenRouter?.ApiKey), ct);
        });

        MapShared(routes, HouseholdConfigProtocol.SettingsView, (store, household, ct) =>
            SectionAsync(store, household, HouseholdSections.SettingsSchema, ct));
        MapShared(routes, HouseholdConfigProtocol.SpacesView, (store, household, ct) =>
            SectionAsync(store, household, HouseholdSections.SpacesSchema, ct));
        MapShared(routes, HouseholdConfigProtocol.IdealistaView, async (store, household, ct) =>
            (await SectionAsync(store, household, HouseholdSections.KeysSchema, ct)).Idealista);

        // Every household's bots, asked with no household: the Telegram server polls them from its
        // start, before any call could prove one. A household whose secrets do not open is left out
        // rather than taking every other household's bots down with it.
        routes.MapPost(Path + HouseholdConfigProtocol.TelegramBotsView, async (
            HttpContext http, HouseholdConfigStore store, ILogger<HouseholdConfigStore> logger, CancellationToken ct) =>
        {
            if (InternalCallers.Reading(http, HouseholdConfigProtocol.TelegramBotsView).Count == 0)
            {
                return InternalCallers.NotYours;
            }

            var households = await store.HouseholdsWithAsync(HouseholdSections.Keys, ct);
            var bots = new List<HouseholdTelegramBot>();
            foreach (var household in households)
            {
                try
                {
                    var keys = await store.ReadAsync(household, HouseholdSections.KeysSchema, ct);
                    bots.AddRange((keys?.TelegramBots ?? new Dictionary<string, TelegramBotKey>())
                        .Select(bot => new HouseholdTelegramBot(household, bot.Key, bot.Value.Token)));
                }
                catch (HouseholdSecretsUnavailableException ex)
                {
                    logger.LogWarning("Left household {Household}'s Telegram bots out: {Reason}", household, ex.Message);
                }
            }

            return Results.Ok(new HouseholdTelegramBots(bots));
        });
    }

    private static void MapShared<T>(
        IEndpointRouteBuilder routes, string view, Func<HouseholdConfigStore, ConfiguredHousehold, CancellationToken, Task<T>> read) =>
        routes.MapPost(Path + view, async (
            HttpContext http, HouseholdConfigQuery query, HouseholdProofs proofs, HouseholdConfigStore store,
            ILogger<HouseholdConfigStore> logger, CancellationToken ct) =>
        {
            var callers = InternalCallers.Reading(http, view);
            if (callers.Count == 0)
            {
                return InternalCallers.NotYours;
            }

            return await AnswerAsync(await proofs.SharedAsync(query, callers, ct), store, logger, async household =>
                new HouseholdConfigAnswer<T>(household.Id, await read(store, household, ct)), ct);
        });

    private static async Task<IResult> AnswerAsync<T>(
        HouseholdProven proven, HouseholdConfigStore store, ILogger logger, Func<ConfiguredHousehold, Task<T>> view,
        CancellationToken ct)
    {
        if (proven is HouseholdProven.Refused refused)
        {
            return Results.Json(refused.Refusal, statusCode: refused.Status);
        }

        var id = ((HouseholdProven.Household)proven).Id;
        if (await store.HouseholdAsync(id, ct) is not { } household)
        {
            return Results.NotFound(new HouseholdConfigRefused(HouseholdConfigRefusal.UnknownHousehold, $"there is no household {id}"));
        }

        try
        {
            return Results.Ok(await view(household));
        }
        catch (HouseholdSecretsUnavailableException ex)
        {
            logger.LogError("Could not answer household {Household}'s configuration: {Reason}", household.Id, ex.Message);
            return Results.Json(new HouseholdConfigRefused(HouseholdConfigRefusal.SecretsUnavailable, ex.Message),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    // The household exists — the answer checked — so the section always reads, if only as its defaults.
    private static async Task<T> SectionAsync<T>(
        HouseholdConfigStore store, ConfiguredHousehold household, SectionSchema<T> schema, CancellationToken ct) where T : class =>
        await store.ReadAsync(household.Id, schema, ct)
        ?? throw new InvalidOperationException($"household {household.Id} went away while its configuration was read");
}