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
    IReadOnlyList<MealItem> Items);

public record SaveMealRequest(
    DateTimeOffset EatenAt,
    string? Notes,
    MealOutcome Outcome,
    string? Photo,
    Interval? FirstEstimate,
    IReadOnlyList<MealItem>? Items);

// Her log as the app keeps it, ported from the standalone app as it was. Saving, editing a portion
// and recording how her glucose went are all a whole meal sent again.
public static class MealEndpoints
{
    private const string PhotoPrefix = "data:image/jpeg;base64,";
    private const int MaxPhotoBytes = 400 * 1024;
    private const int MaxItems = 50;
    private const double MaxCarbs = 2000;

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
        return [.. rows.Select(m => new MealDto(
            m.Id, m.EatenAt, m.Notes, m.Outcome, m.HasPhoto, m.FirstEstimate, InOrder(m.Items)))];
    }

    private static async Task<IResult> CreateAsync(SaveMealRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
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
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/meals/{meal.Id}", ToDto(meal));
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
        return Results.Ok(ToDto(meal));
    }

    private static async Task<IResult> DeleteAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.Meals.Where(m => m.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();

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
        meal.CarbsLow = Math.Round(meal.Items.Sum(item => item.CarbsLow), 1);
        meal.Carbs = Math.Round(meal.Items.Sum(item => item.Carbs), 1);
        meal.CarbsHigh = Math.Round(meal.Items.Sum(item => item.CarbsHigh), 1);
    }

    private static MealDto ToDto(Meal meal) =>
        new(meal.Id, meal.EatenAt, meal.Notes, meal.Outcome, meal.Photo is not null, meal.FirstEstimate,
            InOrder(meal.Items));

    private static IReadOnlyList<MealItem> InOrder(IEnumerable<MealItem> items) => [.. items.OrderBy(i => i.Position)];

    private static string? Validate(SaveMealRequest request) => request switch
    {
        { Items: null or { Count: 0 } } => "A meal needs at least one item.",
        { Items.Count: > MaxItems } => $"A meal can have at most {MaxItems} items.",
        _ when request.Items.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200) =>
            "Every item needs a name of at most 200 characters.",
        _ when request.Items.Any(item => !IsOrdered(item.CarbsLow, item.Carbs, item.CarbsHigh)) =>
            "Carbs must satisfy 0 ≤ low ≤ best ≤ high.",
        { FirstEstimate: { } first } when !IsOrdered(first.Low, first.Best, first.High) =>
            "The first estimate must satisfy 0 ≤ low ≤ best ≤ high.",
        { Notes.Length: > 2000 } => "Notes can be at most 2000 characters.",
        _ when !Enum.IsDefined(request.Outcome) => "Unknown outcome.",
        _ when request.Photo is not null && DecodePhoto(request.Photo) is null =>
            "The photo must be a JPEG data URL under 400 KB.",
        _ => null
    };

    private static bool IsOrdered(double low, double best, double high) =>
        low >= 0 && low <= best && best <= high && high <= MaxCarbs;

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