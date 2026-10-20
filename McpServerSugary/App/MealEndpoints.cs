using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record MealDto(
    Guid Id,
    DateTimeOffset EatenAt,
    string? Notes,
    MealOutcome Outcome,
    bool HasPhoto,
    Interval? FirstEstimate,
    IReadOnlyList<MealItem> Items,
    IReadOnlyList<LinkedDose> Doses);

// A dose linked to a meal, as much of it as the Log needs to show the meal's insulin. Shared says it
// was for other meals too, whose insulin it also is, whole.
public record LinkedDose(Guid Id, double Units, DoseKind Kind, bool Shared = false);

public record SaveMealRequest(
    DateTimeOffset EatenAt,
    string? Notes,
    MealOutcome Outcome,
    string? Photo,
    Interval? FirstEstimate,
    IReadOnlyList<MealItem>? Items,
    MealDoseRequest? Dose = null);

// The rapid insulin she took for a meal, noted as she saves it. Only units and a note: it is rapid
// and taken at the meal's time by definition, and she can change either from the Log afterwards.
public record MealDoseRequest(double? Units, string? Note);

// Her log as the app keeps it, ported from the standalone app as it was. Saving, editing a portion
// and recording how her glucose went are all a whole meal sent again; a meal's doses are not part
// of that, and change only through their own routes.
public static class MealEndpoints
{
    private const string PhotoPrefix = "data:image/jpeg;base64,";
    private const int MaxPhotoBytes = 400 * 1024;

    public static void MapMealEndpoints(this IEndpointRouteBuilder api)
    {
        var meals = api.MapGroup("/meals");
        meals.MapGet("/", ListAsync);
        meals.MapPost("/", CreateAsync);
        meals.MapPut("/{id:guid}", UpdateAsync);
        meals.MapDelete("/{id:guid}", DeleteAsync);
        meals.MapGet("/{id:guid}/photo", PhotoAsync);
    }

    private static async Task<IReadOnlyList<MealDto>> ListAsync(
        SugaryDbContext db, CancellationToken ct, int skip = 0, int take = 50)
    {
        var rows = await db.Meals.AsNoTracking()
            .OrderByDescending(m => m.EatenAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .Select(m => new
            {
                m.Id,
                m.EatenAt,
                m.Notes,
                m.Outcome,
                HasPhoto = m.Photo != null,
                m.FirstEstimate,
                m.Items
            })
            .ToListAsync(ct);
        var doses = await LinkedAsync(db, [.. rows.Select(m => m.Id)], ct);
        return [.. rows.Select(m => new MealDto(
            m.Id, m.EatenAt, m.Notes, m.Outcome, m.HasPhoto, m.FirstEstimate, InOrder(m.Items),
            doses[m.Id].ToList()))];
    }

    private static async Task<IResult> CreateAsync(SaveMealRequest request, SugaryDbContext db, CancellationToken ct)
    {
        // A refused dose refuses the whole save, so the meal is never kept without the dose she meant
        // to record with it.
        if ((Validate(request) ?? DoseProblem(request.Dose)) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        // The first estimate is a record of what happened, so it is set here and never edited.
        var meal = new Meal
        {
            Id = Guid.NewGuid(),
            Photo = DecodePhoto(request.Photo),
            FirstEstimate = request.FirstEstimate
        };
        Apply(request, meal);
        db.Meals.Add(meal);
        // Added before the one save, which is one transaction: both are kept or neither is.
        var dose = request.Dose is { } taken
            ? new Dose
            {
                Id = Guid.NewGuid(),
                TakenAt = meal.EatenAt,
                Units = taken.Units!.Value,
                Kind = DoseKind.Rapid,
                Note = DoseRules.Note(taken.Note),
                MealIds = [meal.Id]
            }
            : null;
        if (dose is not null)
        {
            db.Doses.Add(dose);
        }

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/meals/{meal.Id}",
            ToDto(meal, dose is null ? [] : [new LinkedDose(dose.Id, dose.Units, dose.Kind)]));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, SaveMealRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        if (await db.Meals.FindAsync([id], ct) is not { } meal)
        {
            return Results.NotFound();
        }

        Apply(request, meal);
        // An edit that sends no photo keeps the one already stored.
        meal.Photo = DecodePhoto(request.Photo) ?? meal.Photo;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(meal, (await LinkedAsync(db, [meal.Id], ct))[meal.Id].ToList()));
    }

    // A dose is a fact of what she injected, so it outlives a meal logged wrongly: the meal comes off
    // its list in the same transaction the meal goes in, and any other meal it was for keeps it.
    private static async Task<IResult> DeleteAsync(Guid id, SugaryDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var linked = await db.Doses.Where(d => d.MealIds.Contains(id)).ToListAsync(ct);
        linked.ForEach(dose => dose.MealIds = [.. dose.MealIds.Where(meal => meal != id)]);
        await db.SaveChangesAsync(ct);
        if (await db.Meals.Where(m => m.Id == id).ExecuteDeleteAsync(ct) == 0)
        {
            return Results.NotFound();
        }

        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    // The doses linked to these meals, one query for the lot.
    private static async Task<ILookup<Guid, LinkedDose>> LinkedAsync(
        SugaryDbContext db, IReadOnlyList<Guid> meals, CancellationToken ct)
    {
        var doses = await db.Doses.AsNoTracking()
            .Where(d => d.MealIds.Any(meal => meals.Contains(meal)))
            .OrderBy(d => d.TakenAt)
            .ToListAsync(ct);
        return doses
            .SelectMany(d => d.MealIds.Where(meals.Contains)
                .Select(meal => (meal, linked: new LinkedDose(d.Id, d.Units, d.Kind, d.MealIds.Count > 1))))
            .ToLookup(entry => entry.meal, entry => entry.linked);
    }

    private static string? DoseProblem(MealDoseRequest? dose) =>
        dose is null ? null : DoseRules.Problem(dose.Units, DoseKind.Rapid, dose.Note);

    private static async Task<IResult> PhotoAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.Meals.Where(m => m.Id == id).Select(m => m.Photo).FirstOrDefaultAsync(ct) is { } photo
            ? Results.File(photo, "image/jpeg")
            : Results.NotFound();

    private static void Apply(SaveMealRequest request, Meal meal)
    {
        meal.EatenAt = request.EatenAt;
        meal.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        meal.Outcome = request.Outcome;
        meal.Items = [.. request.Items!.Select((item, position) => item with { Name = item.Name.Trim(), Position = position })];
        MealRules.Total(meal);
    }

    private static MealDto ToDto(Meal meal, IReadOnlyList<LinkedDose> doses) =>
        new(meal.Id, meal.EatenAt, meal.Notes, meal.Outcome, meal.Photo is not null, meal.FirstEstimate,
            InOrder(meal.Items), doses);

    private static IReadOnlyList<MealItem> InOrder(IEnumerable<MealItem> items) => [.. items.OrderBy(i => i.Position)];

    private static string? Validate(SaveMealRequest request) =>
        MealRules.MealProblem(request.Items, request.FirstEstimate, request.Notes, request.Outcome)
        ?? (request.Photo is not null && DecodePhoto(request.Photo) is null
            ? "The photo must be a JPEG data URL under 400 KB."
            : null);

    private static byte[]? DecodePhoto(string? dataUrl)
    {
        if (dataUrl?.StartsWith(PhotoPrefix, StringComparison.Ordinal) is not true)
        {
            return null;
        }

        var buffer = new byte[MaxPhotoBytes];
        return Convert.TryFromBase64String(dataUrl[PhotoPrefix.Length..], buffer, out var written) && written > 0
            ? buffer[..written]
            : null;
    }
}