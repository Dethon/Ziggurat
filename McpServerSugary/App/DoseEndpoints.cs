using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record DoseDto(Guid Id, DateTimeOffset TakenAt, double Units, DoseKind Kind, string? Note, Guid? MealId);

// Kind is text rather than the enum so a kind the app does not know is refused with a sentence,
// where a failed bind would be a bare 400; units are nullable for the same reason.
public record SaveDoseRequest(DateTimeOffset TakenAt, double? Units, string? Kind, string? Note, Guid? MealId);

// Her insulin doses, recorded by her and only ever recorded: nothing here computes, suggests or
// fills in a number of units.
public static class DoseEndpoints
{
    public static void MapDoseEndpoints(this IEndpointRouteBuilder api)
    {
        var doses = api.MapGroup("/doses");
        doses.MapGet("/", ListAsync);
        doses.MapPost("/", CreateAsync);
        doses.MapPut("/{id:guid}", ReplaceAsync);
        doses.MapDelete("/{id:guid}", DeleteAsync);
    }

    // Newest first and paged, as meals are, and bounded to [from, to) when the Log asks for the days
    // it shows.
    private static async Task<IReadOnlyList<DoseDto>> ListAsync(
        SugaryDbContext db, CancellationToken ct, DateTimeOffset? from = null, DateTimeOffset? to = null,
        int skip = 0, int take = 50)
    {
        var lower = from ?? DateTimeOffset.MinValue;
        var upper = to ?? DateTimeOffset.MaxValue;
        var rows = await db.Doses.AsNoTracking()
            .Where(d => d.TakenAt >= lower && d.TakenAt < upper)
            .OrderByDescending(d => d.TakenAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    private static async Task<IResult> CreateAsync(SaveDoseRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (await ValidateAsync(request, db, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var dose = new Dose { Id = Guid.NewGuid() };
        Apply(request, dose);
        db.Doses.Add(dose);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/doses/{dose.Id}", ToDto(dose));
    }

    private static async Task<IResult> ReplaceAsync(
        Guid id, SaveDoseRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (await db.Doses.FindAsync([id], ct) is not { } dose)
        {
            return Results.NotFound();
        }

        if (await ValidateAsync(request, db, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        Apply(request, dose);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(dose));
    }

    private static async Task<IResult> DeleteAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.Doses.Where(d => d.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();

    private static void Apply(SaveDoseRequest request, Dose dose)
    {
        dose.TakenAt = request.TakenAt;
        dose.Units = request.Units!.Value;
        dose.Kind = DoseRules.Kind(request.Kind)!.Value;
        dose.Note = DoseRules.Note(request.Note);
        dose.MealId = request.MealId;
    }

    private static async Task<string?> ValidateAsync(SaveDoseRequest request, SugaryDbContext db, CancellationToken ct) =>
        DoseRules.Problem(request.Units, DoseRules.Kind(request.Kind), request.Note)
        ?? (request.MealId is { } mealId && !await db.Meals.AnyAsync(m => m.Id == mealId, ct)
            ? DoseRules.UnknownMeal
            : null);

    public static DoseDto ToDto(Dose dose) =>
        new(dose.Id, dose.TakenAt, dose.Units, dose.Kind, dose.Note, dose.MealId);
}