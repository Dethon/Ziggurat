using System.Text;
using Domain.Contracts;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using McpServerSugary.Import;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpServerSugary.App;

// The downloads in the app's settings: the same tables the /sugary mount renders, from the
// same rendering, as one file each rather than one per month. Times are in the clock the app asks
// for — the phone's own — or UTC when it names none, as the standalone app wrote them.
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder api)
    {
        var export = api.MapGroup("/export");
        export.MapGet("/meals.csv", MealsAsync);
        export.MapGet("/items.csv", ItemsAsync);
        export.MapGet("/foods.csv", FoodsAsync);
        export.MapGet("/doses.csv", DosesAsync);
    }

    // Named by the same pattern as the three the import reads, though the import does not read it.
    private const string DosesFile = "sugary-doses.csv";

    private static async Task<IResult> MealsAsync(
        ISugaryLog log, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        string? tz = null) =>
        Clock(tz) is { } zone
            ? File(SugaryTables.Meals(await InPeriod(log, from, to, ct), zone), SugaryImport.MealsFile)
            : UnknownClock(tz);

    private static async Task<IResult> ItemsAsync(
        ISugaryLog log, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        string? tz = null) =>
        Clock(tz) is { } zone
            ? File(SugaryTables.Items(await InPeriod(log, from, to, ct), zone), SugaryImport.ItemsFile)
            : UnknownClock(tz);

    private static async Task<IResult> DosesAsync(
        ISugaryLog log, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        string? tz = null) =>
        Clock(tz) is { } zone
            ? File(SugaryTables.Doses(
                await log.DosesAsync(from ?? DateTimeOffset.MinValue, to ?? DateTimeOffset.MaxValue, ct), zone), DosesFile)
            : UnknownClock(tz);

    private static async Task<IResult> FoodsAsync(ISugaryLog log, CancellationToken ct) =>
        File(SugaryTables.Foods(await log.FoodsAsync(ct)), SugaryImport.FoodsFile);

    private static Task<IReadOnlyList<LoggedMeal>> InPeriod(
        ISugaryLog log, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        log.MealsAsync(from ?? DateTimeOffset.MinValue, to ?? DateTimeOffset.MaxValue, ct);

    private static TimeZoneInfo? Clock(string? tz) =>
        string.IsNullOrWhiteSpace(tz) ? TimeZoneInfo.Utc
        : TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var zone) ? zone
        : null;

    // UTF-8 without a byte-order mark, which pandas, DuckDB and a spreadsheet import all read.
    private static IResult File(string csv, string name) =>
        Results.File(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(csv), "text/csv", name);

    private static IResult UnknownClock(string? tz) => Results.Problem(
        $"'{tz}' is not a time zone. Use a name such as Europe/Madrid.", statusCode: StatusCodes.Status400BadRequest);
}