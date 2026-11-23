using System.Globalization;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using McpServerSugary.Glucose;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record WidgetStateDto(DateTimeOffset? CreatedAt);

// The one time the token is ever answered: only its hash is kept.
public record WidgetMintedDto(string Token, DateTimeOffset CreatedAt);

// What a widget shows, ready to show: it has no way to convert a unit or pick a colour. `At` is the
// reading's instant, so the widget works out its age when it draws rather than showing the age it had
// when last fetched, spelled in whole UTC seconds as KWGT's date parser documents. The newest reading is
// the sensor's own, as /current's: a low never waits on a filter.
public record WidgetNowDto(
    string? At, double? MgPerDl, string Value, string Unit, GlucoseTrend Trend, string Arrow, string Band, string Color,
    int TargetLow, int TargetHigh);

public static class WidgetEndpoints
{
    private const int NarrowestCurve = 120;
    private const int WidestCurve = 1200;
    private const int LowestCurve = 60;
    private const int TallestCurve = 800;

    private static readonly int[] _curveHours = [3, 6, 12, 24];

    // The app's: whether this log has a widget address, making one, and taking it back.
    public static void MapWidgetTokenEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/widget", ReadAsync);
        api.MapPut("/widget", MintAsync);
        api.MapDelete("/widget", RemoveAsync);
    }

    // The widget's: read only, and admitted by its token (WidgetAccess) rather than the proxy's check.
    public static void MapWidgetEndpoints(this IEndpointRouteBuilder app)
    {
        var widget = app.MapGroup("/widget");
        widget.MapGet("/{token}/now.json", NowAsync);
        widget.MapGet("/{token}/curve.png", CurveAsync);
        widget.MapFallback(() => Results.NotFound());
    }

    private static async Task<WidgetStateDto> ReadAsync(Person person, SugaryStore store, CancellationToken ct)
    {
        await using var own = store.For(person.Account);
        var createdAt = await own.WidgetTokens.AsNoTracking()
            .Where(t => t.LogUserId == person.Log.UserId)
            .Select(t => (DateTimeOffset?)t.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return new WidgetStateDto(createdAt);
    }

    // Tied to the session asking, so it ends with it; a request whose assertion names none cannot be.
    private static async Task<IResult> MintAsync(Person person, SugaryStore store, TimeProvider clock, CancellationToken ct)
    {
        if (person.SessionId is not { } session)
        {
            return Results.Problem("This request names no session.", statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "no-session" });
        }

        var token = WidgetAccess.NewToken();
        var createdAt = clock.GetUtcNow();
        await using var own = store.For(person.Account);
        await using var transaction = await own.Database.BeginTransactionAsync(ct);
        await own.WidgetTokens.Where(t => t.LogUserId == person.Log.UserId).ExecuteDeleteAsync(ct);
        own.WidgetTokens.Add(new WidgetToken
        {
            Hash = WidgetAccess.Hash(token),
            LogUserId = person.Log.UserId,
            SessionId = session,
            CreatedAt = createdAt
        });
        await own.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new WidgetMintedDto(token, createdAt));
    }

    private static async Task<IResult> RemoveAsync(Person person, SugaryStore store, CancellationToken ct)
    {
        await using var own = store.For(person.Account);
        await own.WidgetTokens.Where(t => t.LogUserId == person.Log.UserId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> NowAsync(string? theme, SugaryDbContext db, CancellationToken ct)
    {
        if (PaletteOf(theme) is not { } palette)
        {
            return BadCurve("A theme is dark or light.");
        }

        var link = await GlucoseEndpoints.LinkOf(db, ct);
        var unit = link?.Unit ?? GlucoseUnit.Mgdl;
        var (low, high) = (link?.TargetLow ?? GlucoseRange.DefaultLow, link?.TargetHigh ?? GlucoseRange.DefaultHigh);
        var newest = await db.GlucoseReadings.AsNoTracking().OrderByDescending(r => r.At).FirstOrDefaultAsync(ct);
        var unitName = unit == GlucoseUnit.Mmol ? "mmol/L" : "mg/dL";
        return Results.Ok(newest is null
            ? new WidgetNowDto(null, null, "--", unitName, GlucoseTrend.Unknown, "", "none", "#8a8a8a", low, high)
            : new WidgetNowDto(
                newest.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), newest.MgPerDl,
                Shown(newest.MgPerDl, unit), unitName, newest.Trend, ArrowOf(newest.Trend),
                BandOf(newest.MgPerDl, low, high), Css(palette.Of(newest.MgPerDl, low, high)), low, high));
    }

    private static async Task<IResult> CurveAsync(
        int? hours, int? width, int? height, string? theme, SugaryDbContext db, TimeProvider clock, HttpContext http,
        CancellationToken ct)
    {
        var (span, wide, tall) = (hours ?? 6, width ?? 600, height ?? 240);
        if (!_curveHours.Contains(span))
        {
            return BadCurve("A curve is over 3, 6, 12 or 24 hours.");
        }

        if (wide is < NarrowestCurve or > WidestCurve || tall is < LowestCurve or > TallestCurve)
        {
            return BadCurve($"A curve is {NarrowestCurve}-{WidestCurve} wide and {LowestCurve}-{TallestCurve} high.");
        }

        if (PaletteOf(theme) is not { } palette)
        {
            return BadCurve("A theme is dark or light.");
        }

        var to = clock.GetUtcNow();
        var from = to.AddHours(-span);
        var link = await GlucoseEndpoints.LinkOf(db, ct);
        var (low, high) = (link?.TargetLow ?? GlucoseRange.DefaultLow, link?.TargetHigh ?? GlucoseRange.DefaultHigh);
        var readings = await GlucoseEndpoints.ReadingsIn(db, from, to, GlucoseView.Smoothed, ct);
        var png = GlucoseCurvePng.Draw(
            readings.Select(r => new GlucosePoint(r.At, r.MgPerDl)), from, to, low, high, wide, tall, palette);
        return Results.File(png, "image/png");
    }

    private static GlucosePalette? PaletteOf(string? theme) => theme switch
    {
        null or "dark" => GlucosePalette.Dark,
        "light" => GlucosePalette.Light,
        _ => null
    };

    private static string Shown(double mgPerDl, GlucoseUnit unit) => unit == GlucoseUnit.Mmol
        ? (mgPerDl / SugaryTables.MgDlPerMmolL).ToString("0.0", CultureInfo.InvariantCulture)
        : Math.Round(mgPerDl).ToString("0", CultureInfo.InvariantCulture);

    private static string ArrowOf(GlucoseTrend trend) => trend switch
    {
        GlucoseTrend.RisingFast => "↑",
        GlucoseTrend.Rising => "↗",
        GlucoseTrend.Steady => "→",
        GlucoseTrend.Falling => "↘",
        GlucoseTrend.FallingFast => "↓",
        _ => ""
    };

    // As the app's `band`: her range decides in range, the clinic's thresholds very low and very high.
    private static string BandOf(double mgPerDl, int low, int high) =>
        mgPerDl < GlucoseStatistics.VeryLowBelow ? "veryLow"
        : mgPerDl < low ? "low"
        : mgPerDl <= high ? "inRange"
        : mgPerDl > GlucoseStatistics.VeryHighAbove ? "veryHigh"
        : "high";

    private static string Css(Rgba colour) => $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";

    private static IResult BadCurve(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "bad-widget-request" });
}