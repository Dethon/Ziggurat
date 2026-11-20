using Domain.DTOs.Sugary;
using McpServerSugary.Glucose;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using GlucoseTrend = Domain.DTOs.Sugary.GlucoseTrend;
using SensorReading = Domain.DTOs.Sugary.SensorReading;

namespace McpServerSugary.App;

public record GlucoseConnectionDto(string PatientId, string Name);

// Never her password; her email in full, since she is the one asking and typed it.
public record GlucoseLinkDto(
    string Email,
    string? Region,
    string? PatientId,
    string? Name,
    IReadOnlyList<GlucoseConnectionDto> Connections,
    GlucoseUnit Unit,
    int TargetLow,
    int TargetHigh,
    DateTimeOffset LinkedAt,
    DateTimeOffset? LastSuccessAt,
    string? LastFailure,
    string? FailureDetail,
    DateTimeOffset? FailingSince);

// Her newest reading and how old it was when asked, and everything the tab reads beside it: the link
// (null when she has none; her readings outlive it), whether a link could be made here, and how she
// reads glucose, defaulting to mg/dL against 70–180 without a link.
public record GlucoseNowDto(
    GlucoseReadingDto? Reading, GlucoseLinkDto? Link, bool CanLink, GlucoseUnit Unit, int TargetLow, int TargetHigh);

public record GlucoseReadingDto(DateTimeOffset At, double MgPerDl, GlucoseTrend Trend, int AgeSeconds);

// BucketMinutes is null where the points are the readings as kept.
public record GlucoseCurveDto(DateTimeOffset From, DateTimeOffset To, int? BucketMinutes, IReadOnlyList<GlucosePoint> Points);

// Her finished cycles' phases (CyclePhases): each phase's figures null under its coverage floor.
// HasReadings: any reading of hers at all, so the app can say "not yet" to someone wearing a sensor.
public record CyclePhasesDto(
    int Cycles, bool HasReadings, GlucoseUnit Unit, int TargetLow, int TargetHigh, PhaseFigures Menstrual, PhaseFigures Follicular,
    PhaseFigures Luteal);

// Shares are fractions of the time with readings, cumulative bands (GlucoseStatistics); null with none.
public record GlucoseStatisticsDto(
    int Days,
    DateTimeOffset From,
    DateTimeOffset To,
    int TargetLow,
    int TargetHigh,
    int Count,
    double Coverage,
    double? Mean,
    double? Gmi,
    double? Cv,
    double? VeryLow,
    double? Low,
    double? InRange,
    double? High,
    double? VeryHigh);

public record LinkGlucoseRequest(string? Email, string? Password, string? Region);

public record ChooseConnectionRequest(string? PatientId);

// Unit is text and the range nullable, so a missing or unknown value is refused with a code rather
// than a bare 400 from a failed bind. Loaded is what the sheet opened with (StaleEdits).
public record GlucoseSettingsRequest(
    string? Unit, int? TargetLow, int? TargetHigh, GlucoseSettingsRequest? Loaded = null);

// The LibreLinkUp account her glucose comes from. Every refusal carries a `code` the app words in her
// language: linking-off, missing-credentials, unknown-region, credentials-refused, step-required (with
// `component`), no-connections, rate-limited (with `retryAfterSeconds` where LibreLinkUp said),
// unavailable, not-linked, unknown-connection, unknown-unit, bad-range; and for what she reads,
// bad-window, window-too-long, bad-period.
public static class GlucoseEndpoints
{
    public static void MapGlucoseEndpoints(this IEndpointRouteBuilder api)
    {
        var glucose = api.MapGroup("/glucose");
        glucose.MapGet("/link", ReadAsync);
        // Logging in may take LibreLinkUp's whole timeout; her turn is taken around the write alone.
        glucose.MapPut("/link", LinkAsync).WithMetadata(new TakesItsOwnTurn());
        glucose.MapPut("/link/connection", ChooseAsync);
        glucose.MapPut("/settings", SaveSettingsAsync);
        glucose.MapDelete("/link", UnlinkAsync);
        glucose.MapGet("/current", CurrentAsync);
        glucose.MapGet("/readings", ReadingsAsync);
        glucose.MapGet("/statistics", StatisticsAsync);
        glucose.MapGet("/cycle", CycleAsync);
    }

    // The periods the tab offers; the clinic's report is fourteen days, ninety the longest kept view.
    private static readonly int[] _periods = [1, 7, 14, 90];

    private static async Task<GlucoseNowDto> CurrentAsync(
        SugaryDbContext db, Person person, GlucoseCredentials credentials, TimeProvider clock, CancellationToken ct)
    {
        var link = await LinkOf(db, ct);
        var newest = await db.GlucoseReadings.AsNoTracking().OrderByDescending(r => r.At).FirstOrDefaultAsync(ct);
        var reading = newest is null
            ? null
            : new GlucoseReadingDto(newest.At, newest.MgPerDl, newest.Trend,
                (int)Math.Max(0, (clock.GetUtcNow() - newest.At).TotalSeconds));
        return new GlucoseNowDto(
            reading,
            link is null ? null : ToDto(link, person.Viewing),
            credentials.CanSeal,
            link?.Unit ?? GlucoseUnit.Mgdl,
            link?.TargetLow ?? GlucoseRange.DefaultLow,
            link?.TargetHigh ?? GlucoseRange.DefaultHigh);
    }

    // The last day's with no window named.
    private static async Task<IResult> ReadingsAsync(
        DateTimeOffset? from, DateTimeOffset? to, SugaryDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-1);
        if (start >= end)
        {
            return Refuse(StatusCodes.Status400BadRequest, "bad-window", "A window starts before it ends.");
        }

        if (end - start > GlucoseCurve.LongestWindow)
        {
            return Refuse(StatusCodes.Status400BadRequest, "window-too-long", "A window is ninety days at most.");
        }

        var bucket = GlucoseCurve.BucketFor(end - start);
        var readings = await ReadingsIn(db, start, end, ct);
        return Results.Ok(new GlucoseCurveDto(
            start, end, bucket is { } size ? (int)size.TotalMinutes : null, GlucoseCurve.Of(readings, bucket)));
    }

    private static async Task<IResult> StatisticsAsync(int? days, SugaryDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var period = days ?? 14;
        if (!_periods.Contains(period))
        {
            return Refuse(StatusCodes.Status400BadRequest, "bad-period", "Figures are over 1, 7, 14 or 90 days.");
        }

        var to = clock.GetUtcNow();
        var from = to.AddDays(-period);
        var link = await LinkOf(db, ct);
        var (low, high) = (link?.TargetLow ?? GlucoseRange.DefaultLow, link?.TargetHigh ?? GlucoseRange.DefaultHigh);
        var figures = GlucoseStatistics.Of(await ReadingsIn(db, from, to, ct), from, to, low, high);
        return Results.Ok(new GlucoseStatisticsDto(
            period, from, to, low, high, figures.Count, figures.Coverage, figures.Mean, figures.Gmi, figures.Cv,
            figures.VeryLow, figures.Low, figures.InRange, figures.High, figures.VeryHigh));
    }

    // Her glucose in each part of her finished cycles (CyclePhases), against her range, mg/dL.
    private static async Task<IResult> CycleAsync(SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        var periods = await db.Periods.AsNoTracking()
            .Select(p => new CyclePeriod(p.Start, p.End))
            .ToListAsync(ct);
        var cycles = CyclePhases.Cycles(periods, calendar.Today);
        var link = await LinkOf(db, ct);
        var (low, high) = (link?.TargetLow ?? GlucoseRange.DefaultLow, link?.TargetHigh ?? GlucoseRange.DefaultHigh);
        // A day either side for the zone: the phases cut the readings at her midnights themselves.
        var readings = cycles.Count == 0
            ? []
            : await ReadingsIn(db,
                new DateTimeOffset(cycles[0].Start.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                new DateTimeOffset(cycles[^1].Luteal.Last.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), ct);
        var figures = CyclePhases.Of(periods, readings, calendar.Zone, calendar.Today, low, high);
        var hasReadings = await db.GlucoseReadings.AnyAsync(ct);
        return Results.Ok(new CyclePhasesDto(
            figures.Cycles, hasReadings, link?.Unit ?? GlucoseUnit.Mgdl, low, high, figures.Menstrual, figures.Follicular, figures.Luteal));
    }

    private static async Task<IReadOnlyList<SensorReading>> ReadingsIn(
        SugaryDbContext db, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await db.GlucoseReadings.AsNoTracking()
            .Where(r => r.At >= from && r.At < to)
            .Select(r => new { r.At, r.MgPerDl })
            .ToListAsync(ct);
        return [.. rows.Select(r => new SensorReading(r.At, r.MgPerDl, GlucoseTrend.Unknown))];
    }

    private static async Task<IResult> ReadAsync(
        SugaryDbContext db, Person person, GlucoseCredentials credentials, CancellationToken ct) =>
        await LinkOf(db, ct) is { } link ? Results.Ok(ToDto(link, person.Viewing))
        : credentials.CanSeal ? NotLinked()
        : LinkingOff();

    // Validated by logging in and listing who the account follows, before anything is kept: a refused
    // link leaves the one she had as it was.
    private static async Task<IResult> LinkAsync(
        LinkGlucoseRequest request, SugaryDbContext db, Person person, GlucoseCredentials credentials,
        LibreLinkUpClient libreLinkUp, OneLogWriteAtATime writes, TimeProvider clock, HttpContext http,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (!credentials.CanSeal)
        {
            return LinkingOff();
        }

        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(request.Password))
        {
            return Refuse(StatusCodes.Status400BadRequest, "missing-credentials", "Type the account's email and password.");
        }

        var region = LibreLinkUpRegions.Normalize(request.Region);
        if (region is null && !string.IsNullOrWhiteSpace(request.Region))
        {
            return Refuse(StatusCodes.Status400BadRequest, "unknown-region", "A region is a code such as eu or us.");
        }

        var answer = await libreLinkUp.ConnectionsAsync(new LibreLinkUpAccount(email, request.Password, region), ct);
        if (answer is LibreLinkUpAnswer<IReadOnlyList<LibreLinkUpConnection>>.Failed failed)
        {
            return Refused(failed.Failure, http, loggers.CreateLogger(typeof(GlucoseEndpoints)));
        }

        var found = (LibreLinkUpAnswer<IReadOnlyList<LibreLinkUpConnection>>.Ok)answer;
        if (found.Value.Count == 0)
        {
            return Refuse(StatusCodes.Status400BadRequest, "no-connections",
                "This account follows nobody. Follow her sensor from the LibreLinkUp app first.");
        }

        var offered = found.Value
            .Select(c => new GlucoseConnectionOffered { PatientId = c.PatientId, Name = NameOf(c) })
            .ToList();
        var now = clock.GetUtcNow();
        using (await writes.TurnAsync(person.Account, ct))
        {
            var link = await LinkOf(db, ct);
            if (link is null)
            {
                link = new GlucoseLink();
                db.GlucoseLinks.Add(link);
            }

            link.Email = email;
            link.SealedPassword = credentials.Seal(person.Account, request.Password);
            link.Region = LibreLinkUpRegions.Normalize(found.Region);
            link.PatientId = offered.Count == 1 ? offered[0].PatientId
                : offered.Any(c => c.PatientId == link.PatientId) ? link.PatientId
                : null;
            link.Connections.Clear();
            link.Connections.AddRange(offered);
            link.LinkedAt = now;
            link.LastSuccessAt = now;
            link.LastFailure = null;
            link.FailureDetail = null;
            link.FailingSince = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(link));
        }
    }

    private static async Task<IResult> ChooseAsync(ChooseConnectionRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (await LinkOf(db, ct) is not { } link)
        {
            return NotLinked();
        }

        if (link.Connections.All(c => c.PatientId != request.PatientId))
        {
            return Refuse(StatusCodes.Status400BadRequest, "unknown-connection",
                "The account follows nobody by that id. Link it again to see who it follows now.");
        }

        link.PatientId = request.PatientId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(link));
    }

    private static async Task<IResult> SaveSettingsAsync(
        GlucoseSettingsRequest request, SugaryDbContext db, HttpContext http, CancellationToken ct)
    {
        if (await LinkOf(db, ct) is not { } link)
        {
            return NotLinked();
        }

        if (request.Loaded is { } loaded)
        {
            var merged = StaleEdits.Merge(http, Compared(loaded), Compared(request),
                new GlucoseSettingsRequest(UnitWord(link.Unit), link.TargetLow, link.TargetHigh));
            if (merged.Value is not { } value)
            {
                return StaleEdits.Conflict(merged.Conflicts);
            }

            request = value;
        }

        if (UnitOf(request.Unit) is not { } unit)
        {
            return Refuse(StatusCodes.Status400BadRequest, "unknown-unit", "Glucose is read in mg/dL or mmol/L.");
        }

        if (!GlucoseRange.IsValid(request.TargetLow, request.TargetHigh))
        {
            return Refuse(StatusCodes.Status400BadRequest, "bad-range",
                $"A target range is from {GlucoseRange.Lowest} to {GlucoseRange.Highest} mg/dL, its low below its high.");
        }

        link.Unit = unit;
        link.TargetLow = request.TargetLow!.Value;
        link.TargetHigh = request.TargetHigh!.Value;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(link));
    }

    // Forgets the account and its password. Her readings are her log's, and stay.
    private static async Task<IResult> UnlinkAsync(SugaryDbContext db, CancellationToken ct)
    {
        if (await LinkOf(db, ct) is not { } link)
        {
            return NotLinked();
        }

        db.GlucoseLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static Task<GlucoseLink?> LinkOf(SugaryDbContext db, CancellationToken ct) =>
        db.GlucoseLinks.Include(l => l.Connections).SingleOrDefaultAsync(ct);

    // The account's password is never logged: LibreLinkUpAccount is a record and would print it.
    private static IResult Refused(LibreLinkUpFailure failure, HttpContext http, ILogger logger)
    {
        switch (failure)
        {
            case LibreLinkUpFailure.CredentialsRefused:
                return Refuse(StatusCodes.Status400BadRequest, "credentials-refused",
                    "LibreLinkUp did not accept that email and password.");
            case LibreLinkUpFailure.StepRequired step:
                return Refuse(StatusCodes.Status400BadRequest, "step-required",
                    "Open the LibreLinkUp app and accept what it asks first.", ("component", step.Component));
            case LibreLinkUpFailure.RateLimited limited:
                var seconds = limited.RetryAfter is { } wait ? (int?)Math.Ceiling(wait.TotalSeconds) : null;
                if (seconds is not null)
                {
                    http.Response.Headers.RetryAfter = seconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return Refuse(StatusCodes.Status429TooManyRequests, "rate-limited",
                    "LibreLinkUp was asked too often. Try again later.", ("retryAfterSeconds", seconds));
            case LibreLinkUpFailure.VersionTooOld old:
                logger.LogWarning("LibreLinkUp no longer serves app version {Version}; it asks for {Minimum}",
                    LibreLinkUpClient.AppVersion, old.MinimumVersion);
                return Unavailable();
            case LibreLinkUpFailure.Unreachable unreachable:
                logger.LogWarning("LibreLinkUp gave no answer to a link: {Reason}", unreachable.Reason);
                return Unavailable();
            default:
                return Unavailable();
        }
    }

    private static IResult Unavailable() =>
        Refuse(StatusCodes.Status502BadGateway, "unavailable", "LibreLinkUp could not be reached. Try again later.");

    private static IResult NotLinked() =>
        Refuse(StatusCodes.Status404NotFound, "not-linked", "No LibreLinkUp account is linked.");

    private static IResult LinkingOff() =>
        Refuse(StatusCodes.Status503ServiceUnavailable, "linking-off",
            "Glucose can't be linked on this server: it has no sealing key.");

    private static IResult Refuse(int status, string code, string detail, params (string Name, object? Value)[] more) =>
        Results.Problem(detail, statusCode: status, extensions: more
            .Prepend(("code", code))
            .ToDictionary<(string Name, object? Value), string, object?>(field => field.Name, field => field.Value));

    private static string NameOf(LibreLinkUpConnection connection) =>
        $"{connection.FirstName} {connection.LastName}".Trim() is { Length: > 0 } name ? name : connection.PatientId;

    private static GlucoseUnit? UnitOf(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "mgdl" => GlucoseUnit.Mgdl,
        "mmol" => GlucoseUnit.Mmol,
        _ => null
    };

    private static string UnitWord(GlucoseUnit unit) => unit == GlucoseUnit.Mmol ? "mmol" : "mgdl";

    private static GlucoseSettingsRequest Compared(GlucoseSettingsRequest request) =>
        request with { Unit = request.Unit?.Trim().ToLowerInvariant(), Loaded = null };

    public static string? FailureCode(GlucoseLinkFailure? failure) => failure switch
    {
        GlucoseLinkFailure.CredentialsRefused => "credentials-refused",
        GlucoseLinkFailure.StepRequired => "step-required",
        GlucoseLinkFailure.VersionTooOld => "unavailable",
        GlucoseLinkFailure.RateLimited => "rate-limited",
        GlucoseLinkFailure.Unreachable => "unavailable",
        GlucoseLinkFailure.CredentialsUnreadable => "credentials-unreadable",
        _ => null
    };

    // A reader of her log (Person.Viewing) sees the link's state and range, never the LibreLinkUp
    // account she signs in with nor the people it follows.
    private static GlucoseLinkDto ToDto(GlucoseLink link, bool viewing = false) => new(
        viewing ? "" : link.Email, link.Region, link.PatientId, link.Name,
        viewing ? [] : [.. link.Connections.Select(c => new GlucoseConnectionDto(c.PatientId, c.Name))],
        link.Unit, link.TargetLow, link.TargetHigh, link.LinkedAt, link.LastSuccessAt,
        FailureCode(link.LastFailure), link.FailureDetail, link.FailingSince);
}