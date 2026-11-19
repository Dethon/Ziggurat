using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record ProfileDto(Sex? Sex, DiabetesType? DiabetesType, DateOnly? DiagnosedOn);

// Words are text so a missing or unknown one is refused with a sentence rather than a bare 400 from a
// failed bind. A field sent empty is one she has not said, or has taken back. Loaded is what the sheet
// opened with (StaleEdits).
public record SaveProfileRequest(
    string? Sex, string? DiabetesType, DateOnly? DiagnosedOn, SaveProfileRequest? Loaded = null);

public record MeasurementDto(Guid Id, BodyMeasure Kind, DateOnly On, double Value);

public record SaveMeasurementRequest(string? Kind, DateOnly? On, double? Value, SaveMeasurementRequest? Loaded = null);

// Who she is, and her height and weight over time: hers alone, read by the agent beside her log
// (the profile, heights and weights tables) and by the app, whose Cycle tab follows her sex.
public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/profile", GetProfileAsync);
        api.MapPut("/profile", SaveProfileAsync);

        var measurements = api.MapGroup("/measurements");
        measurements.MapGet("/", ListMeasurementsAsync);
        measurements.MapPost("/", CreateMeasurementAsync);
        measurements.MapPut("/{id:guid}", ReplaceMeasurementAsync);
        measurements.MapDelete("/{id:guid}", DeleteMeasurementAsync);
    }

    private static async Task<ProfileDto> GetProfileAsync(SugaryDbContext db, CancellationToken ct) =>
        ToDto(await db.Profiles.AsNoTracking().FirstOrDefaultAsync(ct));

    private static async Task<IResult> SaveProfileAsync(
        SaveProfileRequest request, SugaryDbContext db, HomeCalendar calendar, HttpContext http, CancellationToken ct)
    {
        var profile = await db.Profiles.FirstOrDefaultAsync(ct);
        if (request.Loaded is { } loaded)
        {
            var merged = StaleEdits.Merge(http, loaded with { Loaded = null }, request with { Loaded = null },
                AsRequest(ToDto(profile)));
            if (merged.Value is not { } value)
            {
                return StaleEdits.Conflict(merged.Conflicts);
            }

            request = value;
        }

        if (ProfileRules.ProfileProblem(request.Sex, request.DiabetesType, request.DiagnosedOn, calendar.Today) is { } problem)
        {
            return Refuse(problem);
        }

        if (profile is null)
        {
            profile = new Profile();
            db.Profiles.Add(profile);
        }

        profile.Sex = ProfileRules.Word<Sex>(request.Sex);
        profile.DiabetesType = ProfileRules.Word<DiabetesType>(request.DiabetesType);
        profile.DiagnosedOn = request.DiagnosedOn;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(profile));
    }

    // Newest first, a height and a weight of one day in that order.
    private static async Task<IReadOnlyList<MeasurementDto>> ListMeasurementsAsync(SugaryDbContext db, CancellationToken ct)
    {
        var rows = await db.BodyMeasurements.AsNoTracking().ToListAsync(ct);
        return [.. rows.OrderByDescending(m => m.On).ThenBy(m => m.Kind).Select(ToDto)];
    }

    private static async Task<IResult> CreateMeasurementAsync(
        SaveMeasurementRequest request, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (await ValidateAsync(request, null, db, calendar, ct) is { } problem)
        {
            return Refuse(problem);
        }

        var measurement = new BodyMeasurement
        {
            Id = Guid.NewGuid(),
            Kind = ProfileRules.Word<BodyMeasure>(request.Kind)!.Value,
            On = request.On!.Value,
            Value = request.Value!.Value
        };
        db.BodyMeasurements.Add(measurement);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/measurements/{measurement.Id}", ToDto(measurement));
    }

    // A correction of the day or the value. The kind stays: a weight is never corrected into a height.
    private static async Task<IResult> ReplaceMeasurementAsync(
        Guid id, SaveMeasurementRequest request, SugaryDbContext db, HomeCalendar calendar, HttpContext http,
        CancellationToken ct)
    {
        if (await db.BodyMeasurements.FindAsync([id], ct) is not { } measurement)
        {
            return Results.NotFound();
        }

        if (request.Loaded is { } loaded)
        {
            var merged = StaleEdits.Merge(http, loaded with { Loaded = null }, request with { Loaded = null },
                new SaveMeasurementRequest(SugaryTables.Word(measurement.Kind), measurement.On, measurement.Value));
            if (merged.Value is not { } value)
            {
                return StaleEdits.Conflict(merged.Conflicts);
            }

            request = value;
        }

        request = request with { Kind = SugaryTables.Word(measurement.Kind) };
        if (await ValidateAsync(request, id, db, calendar, ct) is { } problem)
        {
            return Refuse(problem);
        }

        measurement.On = request.On!.Value;
        measurement.Value = request.Value!.Value;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(measurement));
    }

    private static async Task<IResult> DeleteMeasurementAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.BodyMeasurements.Where(m => m.Id == id).ExecuteDeleteAsync(ct) > 0
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<string?> ValidateAsync(
        SaveMeasurementRequest request, Guid? self, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (ProfileRules.MeasurementProblem(request.Kind, request.On, request.Value, calendar.Today) is { } problem)
        {
            return problem;
        }

        var kind = ProfileRules.Word<BodyMeasure>(request.Kind)!.Value;
        var day = request.On!.Value;
        return await db.BodyMeasurements.AnyAsync(m => m.Kind == kind && m.On == day && m.Id != self, ct)
            ? ProfileRules.DayTaken(kind)
            : null;
    }

    private static SaveProfileRequest AsRequest(ProfileDto profile) => new(
        profile.Sex is { } sex ? SugaryTables.Word(sex) : null,
        profile.DiabetesType is { } type ? SugaryTables.Word(type) : null,
        profile.DiagnosedOn);

    private static IResult Refuse(string problem) =>
        Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);

    private static ProfileDto ToDto(Profile? profile) =>
        new(profile?.Sex, profile?.DiabetesType, profile?.DiagnosedOn);

    private static MeasurementDto ToDto(BodyMeasurement m) => new(m.Id, m.Kind, m.On, m.Value);
}