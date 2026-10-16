using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record PeriodDto(Guid Id, DateOnly Start, DateOnly? End);

// The start is nullable so a missing one is refused with a sentence rather than stored as the first
// day of the calendar.
public record SavePeriodRequest(DateOnly? Start, DateOnly? End);

// Her periods, recorded by her. What the app forecasts from them is the app's own and never stored.
public static class PeriodEndpoints
{
    public static void MapPeriodEndpoints(this IEndpointRouteBuilder api)
    {
        var periods = api.MapGroup("/periods");
        periods.MapGet("/", ListAsync);
        periods.MapPost("/", CreateAsync);
        periods.MapPut("/{id:guid}", ReplaceAsync);
        periods.MapDelete("/{id:guid}", DeleteAsync);
    }

    // All of them, newest first: a few a year, and the forecast reads them all.
    private static async Task<IReadOnlyList<PeriodDto>> ListAsync(SugaryDbContext db, CancellationToken ct)
    {
        var rows = await db.Periods.AsNoTracking().OrderByDescending(p => p.Start).ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    private static async Task<IResult> CreateAsync(
        SavePeriodRequest request, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (await ValidateAsync(request, null, db, calendar, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var period = new Period { Id = Guid.NewGuid(), Start = request.Start!.Value, End = request.End };
        db.Periods.Add(period);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/periods/{period.Id}", ToDto(period));
    }

    private static async Task<IResult> ReplaceAsync(
        Guid id, SavePeriodRequest request, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        if (await db.Periods.FindAsync([id], ct) is not { } period)
        {
            return Results.NotFound();
        }

        if (await ValidateAsync(request, id, db, calendar, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        period.Start = request.Start!.Value;
        period.End = request.End;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(period));
    }

    private static async Task<IResult> DeleteAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.Periods.Where(p => p.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();

    private static async Task<string?> ValidateAsync(
        SavePeriodRequest request, Guid? self, SugaryDbContext db, HomeCalendar calendar, CancellationToken ct)
    {
        var others = await db.Periods.AsNoTracking().Where(p => p.Id != self).ToListAsync(ct);
        return PeriodRules.Problem(request.Start, request.End, calendar.Today, others);
    }

    private static PeriodDto ToDto(Period period) => new(period.Id, period.Start, period.End);
}