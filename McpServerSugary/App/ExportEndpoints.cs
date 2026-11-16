using System.Text;
using Domain.Contracts;
using Domain.DTOs.Sugary;
using Domain.DTOs.Tables;
using Domain.Tools.Sugary;
using McpServerSugary.Import;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpServerSugary.App;

// The downloads in the app's settings: the same tables the /sugary mount renders, from the
// same rendering, as one file each rather than one per month, and of the asking person's log and
// their household's foods alone. Times are in the clock the app asks for — the phone's own — or UTC
// when it names none, as the standalone app wrote them.
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder api)
    {
        var export = api.MapGroup("/export");
        export.MapGet("/meals.csv", MealsAsync);
        export.MapGet("/items.csv", ItemsAsync);
        export.MapGet("/foods.csv", FoodsAsync);
        export.MapGet("/doses.csv", DosesAsync);
        export.MapGet("/dose-meals.csv", DoseMealsAsync);
        export.MapGet("/exercise.csv", ExerciseAsync);
        export.MapGet("/periods.csv", PeriodsAsync);
        export.MapGet("/day-notes.csv", DayNotesAsync);
    }

    // Named by the same pattern as the three the import reads, though the import does not read it.
    private const string DosesFile = "sugary-doses.csv";

    private static async Task<IResult> MealsAsync(
        ISugaryLog log, Person person, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        string? tz = null) =>
        Clock(tz) is { } zone
            ? File(SugaryTables.Meals(await InPeriod(log, person, from, to, ct), zone), SugaryImport.MealsFile)
            : UnknownClock(tz);

    private static async Task<IResult> ItemsAsync(
        ISugaryLog log, Person person, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        string? tz = null) =>
        Clock(tz) is { } zone
            ? File(SugaryTables.Items(await InPeriod(log, person, from, to, ct), zone), SugaryImport.ItemsFile)
            : UnknownClock(tz);

    private static async Task<IResult> DosesAsync(
        ISugaryLog log, Person person, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        string? tz = null) =>
        Clock(tz) is { } zone
            ? File(SugaryTables.Doses(
                await log.DosesAsync(person.Account, from ?? DateTimeOffset.MinValue, to ?? DateTimeOffset.MaxValue, ct), zone), DosesFile)
            : UnknownClock(tz);

    // Bounded by the doses' times, as doses.csv is, so the two files of one period agree.
    private static async Task<IResult> DoseMealsAsync(
        ISugaryLog log, Person person, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        File(SugaryTables.DoseMeals(
            await log.DosesAsync(person.Account, from ?? DateTimeOffset.MinValue, to ?? DateTimeOffset.MaxValue, ct)), "sugary-dose-meals.csv");

    private static async Task<IResult> ExerciseAsync(
        ISugaryLog log, Person person, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        string? tz = null) =>
        Clock(tz) is { } zone
            ? File(SugaryTables.Exercise(
                    await log.ExerciseAsync(person.Account, from ?? DateTimeOffset.MinValue, to ?? DateTimeOffset.MaxValue, ct), zone),
                "sugary-exercise.csv")
            : UnknownClock(tz);

    // Days carry no clock, so these take no zone.
    private static async Task<IResult> PeriodsAsync(ISugaryLog log, Person person, CancellationToken ct) =>
        File(SugaryTables.Periods(await log.PeriodsAsync(person.Account, ct)), "sugary-periods.csv");

    private static async Task<IResult> DayNotesAsync(ISugaryLog log, Person person, CancellationToken ct) =>
        File(SugaryTables.DayNotes(await log.DayNotesAsync(person.Account, ct)), "sugary-day-notes.csv");

    private static async Task<IResult> FoodsAsync(ISugaryLog log, Person person, CancellationToken ct) =>
        File(SugaryTables.Foods(await log.FoodsAsync(person.Account, ct)), SugaryImport.FoodsFile);

    private static Task<IReadOnlyList<LoggedMeal>> InPeriod(
        ISugaryLog log, Person person, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        log.MealsAsync(person.Account, from ?? DateTimeOffset.MinValue, to ?? DateTimeOffset.MaxValue, ct);

    private static TimeZoneInfo? Clock(string? tz) =>
        string.IsNullOrWhiteSpace(tz) ? TimeZoneInfo.Utc
        : TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var zone) ? zone
        : null;

    // UTF-8 without a byte-order mark, which pandas, DuckDB and a spreadsheet import all read.
    private static IResult File(Table table, string name) =>
        Results.File(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(SugaryCsv.Write(table)), "text/csv", name);

    private static IResult UnknownClock(string? tz) => Results.Problem(
        $"'{tz}' is not a time zone. Use a name such as Europe/Madrid.", statusCode: StatusCodes.Status400BadRequest);
}