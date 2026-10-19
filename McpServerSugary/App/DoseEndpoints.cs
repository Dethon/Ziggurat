using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record DoseDto(
    Guid Id, DateTimeOffset TakenAt, double Units, DoseKind Kind, string? Note, IReadOnlyList<Guid> MealIds);

// Kind is text rather than the enum so a kind the app does not know is refused with a sentence,
// where a failed bind would be a bare 400; the time and the units are nullable for the same reason,
// and so a missing time is refused rather than stored as the first day of the calendar. MealId is
// what an installed app from before a dose could be for several meals still sends, and is heard as
// a list of one.
public record SaveDoseRequest(
    DateTimeOffset? TakenAt, double? Units, string? Kind, string? Note, IReadOnlyList<Guid>? MealIds = null,
    Guid? MealId = null)
{
    // Each meal once, in the order she picked them.
    public IReadOnlyList<Guid> Meals => [.. (MealIds ?? (MealId is { } one ? [one] : [])).Distinct()];
}

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
        dose.TakenAt = request.TakenAt!.Value;
        dose.Units = request.Units!.Value;
        dose.Kind = DoseRules.Kind(request.Kind)!.Value;
        dose.Note = DoseRules.Note(request.Note);
        dose.MealIds = [.. request.Meals];
    }

    private static async Task<string?> ValidateAsync(SaveDoseRequest request, SugaryDbContext db, CancellationToken ct) =>
        (request.TakenAt is null ? DoseRules.NoTime : null)
        ?? DoseRules.Problem(request.Units, DoseRules.Kind(request.Kind), request.Note)
        ?? (await KnownMealsAsync(request.Meals, db, ct) ? null : DoseRules.UnknownMeal);

    private static async Task<bool> KnownMealsAsync(IReadOnlyList<Guid> meals, SugaryDbContext db, CancellationToken ct) =>
        meals.Count == 0 || await db.Meals.CountAsync(m => meals.Contains(m.Id), ct) == meals.Count;

    public static DoseDto ToDto(Dose dose) =>
        new(dose.Id, dose.TakenAt, dose.Units, dose.Kind, dose.Note, dose.MealIds);
}