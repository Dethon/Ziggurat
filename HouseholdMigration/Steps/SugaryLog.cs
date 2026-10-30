using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HouseholdMigration.Steps;

// Sugary's log was one person's before it was per person, and every row from then carries 'legacy' in
// its owner columns, seen by nobody (.claude/rules/sugary.md). The person's rows become the person
// named --sugary's, and the kitchen — foods and recipes — the household's. Through the server's own
// context, with its owner filters set aside as the rules allow for exactly this, so the rows given away
// are the ones those filters then show the person.
public sealed class SugaryLog(string databasePath) : IMigrationStep
{
    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport($"Sugary log at {databasePath}");
        if (!File.Exists(databasePath))
        {
            report.Lines.Add("no log there, so nothing to give");
            return report;
        }

        await using var services = new ServiceCollection()
            .AddSugaryStore(new SugarySettings { DatabasePath = databasePath })
            .BuildServiceProvider();
        var contexts = services.GetRequiredService<IDbContextFactory<SugaryDbContext>>();
        if (!dryRun)
        {
            // What the server does on start: a log it has not opened since its owner columns arrived
            // gets them now, 'legacy' in every row.
            await SugaryDatabase.MigrateAsync(contexts, ct);
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var person = household.SugaryPerson;
        var personal = new[]
        {
            await GiveAsync(db.Meals, person, dryRun, ct),
            await GiveAsync(db.Doses, person, dryRun, ct),
            await GiveAsync(db.Pens, person, dryRun, ct),
            await GiveAsync(db.Periods, person, dryRun, ct),
            await GiveAsync(db.DayNotes, person, dryRun, ct),
            await GiveAsync(db.ExerciseSessions, person, dryRun, ct),
            await GiveAsync(db.PushSubscriptions, person, dryRun, ct)
        }.Sum();
        var kitchen = await ShareAsync(db.Foods, household.TenantId, dryRun, ct)
                      + await ShareAsync(db.Recipes, household.TenantId, dryRun, ct);
        report.Changes = personal + kitchen;
        if (personal > 0)
        {
            report.Lines.Add($"{person.Label}: {Counted.Of(personal, "row")} of the log");
        }

        if (kitchen > 0)
        {
            report.Lines.Add($"the household: {Counted.Of(kitchen, "food")} and recipes");
        }

        return report;
    }

    private static Task<int> GiveAsync<T>(DbSet<T> rows, MigratedPerson person, bool dryRun, CancellationToken ct)
        where T : class, IPersonal
    {
        var unassigned = rows.IgnoreQueryFilters().Where(r => r.UserId == Owners.Unassigned);
        return dryRun
            ? unassigned.CountAsync(ct)
            : unassigned.ExecuteUpdateAsync(
                set => set.SetProperty(r => r.UserId, person.UserId!).SetProperty(r => r.TenantId, person.TenantId), ct);
    }

    private static Task<int> ShareAsync<T>(DbSet<T> rows, string tenantId, bool dryRun, CancellationToken ct)
        where T : class, IHousehold
    {
        var unassigned = rows.IgnoreQueryFilters().Where(r => r.TenantId == Owners.Unassigned);
        return dryRun
            ? unassigned.CountAsync(ct)
            : unassigned.ExecuteUpdateAsync(set => set.SetProperty(r => r.TenantId, tenantId), ct);
    }
}