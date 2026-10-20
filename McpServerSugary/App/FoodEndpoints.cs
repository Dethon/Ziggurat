using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record FoodDto(
    Guid Id,
    string Name,
    double? CarbsPer100g,
    string Unit,
    double? PortionGrams,
    double? PortionCarbs,
    string? Barcode,
    ItemSource Source,
    int Uses,
    DateTimeOffset? LastUsedAt);

public record SaveFoodRequest(
    string? Name,
    double? CarbsPer100g,
    string? Unit,
    double? PortionGrams,
    double? PortionCarbs,
    string? Barcode,
    ItemSource Source);

// Her own foods, ported from the standalone app as they were.
public static class FoodEndpoints
{
    private const double MaxAmount = 5000;
    private const double MaxCarbs = 2000;

    public static void MapFoodEndpoints(this IEndpointRouteBuilder api)
    {
        var foods = api.MapGroup("/foods");
        foods.MapGet("/", ListAsync);
        foods.MapPost("/", CreateAsync);
        foods.MapPut("/{id:guid}", UpdateAsync);
        foods.MapDelete("/{id:guid}", DeleteAsync);
    }

    // The whole list, most recently eaten first: it is one person's kitchen, and the picker
    // filters it as she types.
    private static async Task<IReadOnlyList<FoodDto>> ListAsync(SugaryDbContext db, CancellationToken ct)
    {
        var foods = await db.Foods.AsNoTracking().ToListAsync(ct);
        var eaten = await db.Meals.AsNoTracking()
            .SelectMany(meal => meal.Items, (meal, item) => new { item.FoodId, meal.EatenAt })
            .Where(use => use.FoodId != null)
            .ToListAsync(ct);
        var uses = eaten.GroupBy(use => use.FoodId!.Value)
            .ToDictionary(group => group.Key, group => (Count: group.Count(), Last: group.Max(use => use.EatenAt)));

        return [.. foods
            .Select(food => uses.TryGetValue(food.Id, out var use)
                ? ToDto(food, use.Count, use.Last)
                : ToDto(food, 0, null))
            .OrderByDescending(food => food.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenBy(food => food.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static async Task<IResult> CreateAsync(
        SaveFoodRequest request, SugaryDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var food = new Food { Id = Guid.NewGuid(), CreatedAt = clock.GetUtcNow() };
        Apply(request, food);
        db.Foods.Add(food);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/foods/{food.Id}", ToDto(food, 0, null));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, SaveFoodRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        if (await db.Foods.FindAsync([id], ct) is not { } food)
        {
            return Results.NotFound();
        }

        Apply(request, food);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(food, 0, null));
    }

    // Meals that used the food keep their own copy of its numbers, so removing it changes no log entry.
    private static async Task<IResult> DeleteAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.Foods.Where(f => f.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();

    private static void Apply(SaveFoodRequest request, Food food)
    {
        food.Name = request.Name!.Trim();
        food.CarbsPer100g = request.CarbsPer100g;
        food.Unit = request.Unit ?? "g";
        food.PortionGrams = request.PortionGrams;
        food.PortionCarbs = request.PortionCarbs;
        food.Barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        food.Source = request.Source;
    }

    private static FoodDto ToDto(Food food, int uses, DateTimeOffset? lastUsedAt) => new(
        food.Id, food.Name, food.CarbsPer100g, food.Unit, food.PortionGrams, food.PortionCarbs, food.Barcode,
        food.Source, uses, lastUsedAt);

    private static string? Validate(SaveFoodRequest request) => request switch
    {
        _ when string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 =>
            "A food needs a name of at most 200 characters.",
        { CarbsPer100g: null, PortionCarbs: null } =>
            "A food needs its carbs per 100, or the carbs of a usual portion.",
        { CarbsPer100g: < 0 or > 100 } => "Carbs per 100 must be between 0 and 100.",
        { PortionGrams: <= 0 or > MaxAmount } => $"A usual portion must be above 0 and at most {MaxAmount}.",
        { PortionCarbs: < 0 or > MaxCarbs } => $"A portion's carbs must be between 0 and {MaxCarbs}.",
        { Unit: not (null or "g" or "ml") } => "The unit must be g or ml.",
        { Barcode: { } barcode } when !string.IsNullOrWhiteSpace(barcode) && !Barcodes.IsValid(barcode.Trim()) =>
            "That is not a product barcode.",
        // A food's numbers come from a label, a barcode or the person; a photo estimate is not a fact to keep.
        { Source: ItemSource.Estimate } => "A food cannot come from an estimate.",
        _ when !Enum.IsDefined(request.Source) => "Unknown source.",
        _ => null
    };
}

public static class Barcodes
{
    public static bool IsValid(string barcode) => barcode.Length is >= 8 and <= 14 && barcode.All(char.IsAsciiDigit);
}