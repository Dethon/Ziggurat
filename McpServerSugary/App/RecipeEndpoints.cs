using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record RecipeDto(
    Guid Id,
    string Name,
    double? CookedGrams,
    double? Servings,
    IReadOnlyList<RecipeIngredient> Ingredients,
    int Uses,
    DateTimeOffset? LastUsedAt);

public record SaveRecipeRequest(
    string? Name,
    double? CookedGrams,
    double? Servings,
    IReadOnlyList<RecipeIngredient>? Ingredients);

// Her recipes, kept as their shape alone: which of her foods, how much of each, and what the pot
// makes. The app works out the carbs from her foods as they are now.
public static class RecipeEndpoints
{
    private const double MaxAmount = 5000;
    private const double MaxCookedGrams = 50000;
    private const double MaxServings = 100;
    private const int MaxIngredients = 60;

    public static void MapRecipeEndpoints(this IEndpointRouteBuilder api)
    {
        var recipes = api.MapGroup("/recipes");
        recipes.MapGet("/", ListAsync);
        recipes.MapPost("/", CreateAsync);
        recipes.MapPut("/{id:guid}", UpdateAsync);
        recipes.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<IReadOnlyList<RecipeDto>> ListAsync(SugaryDbContext db, CancellationToken ct)
    {
        var recipes = await db.Recipes.AsNoTracking().ToListAsync(ct);
        return [.. recipes
            .Select(recipe => ToDto(recipe, 0, null))
            .OrderBy(recipe => recipe.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static async Task<IResult> CreateAsync(
        SaveRecipeRequest request, SugaryDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (await ValidateAsync(request, db, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var recipe = new Recipe { Id = Guid.NewGuid(), CreatedAt = clock.GetUtcNow() };
        Apply(request, recipe);
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/recipes/{recipe.Id}", ToDto(recipe, 0, null));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, SaveRecipeRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (await ValidateAsync(request, db, ct) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        if (await db.Recipes.FindAsync([id], ct) is not { } recipe)
        {
            return Results.NotFound();
        }

        Apply(request, recipe);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(recipe, 0, null));
    }

    // Meals that had a helping keep their own copy of its numbers, so removing it changes no log entry.
    private static async Task<IResult> DeleteAsync(Guid id, SugaryDbContext db, CancellationToken ct) =>
        await db.Recipes.Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();

    private static void Apply(SaveRecipeRequest request, Recipe recipe)
    {
        recipe.Name = request.Name!.Trim();
        recipe.CookedGrams = request.CookedGrams;
        recipe.Servings = request.Servings;
        recipe.Ingredients = [.. request.Ingredients!.Select((ingredient, position) => ingredient with { Position = position })];
    }

    private static RecipeDto ToDto(Recipe recipe, int uses, DateTimeOffset? lastUsedAt) => new(
        recipe.Id, recipe.Name, recipe.CookedGrams, recipe.Servings,
        [.. recipe.Ingredients.OrderBy(i => i.Position)], uses, lastUsedAt);

    private static async Task<string?> ValidateAsync(SaveRecipeRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var wanted = request.Ingredients!.Select(i => i.FoodId).Distinct().ToList();
        var kept = await db.Foods.CountAsync(f => wanted.Contains(f.Id), ct);
        return kept == wanted.Count ? null : "Every ingredient must be a food in the pantry.";
    }

    private static string? Validate(SaveRecipeRequest request) => request switch
    {
        _ when string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 =>
            "A recipe needs a name of at most 200 characters.",
        // The yield is what turns the pot's carbs into a helping's, and there is one way to say it.
        { CookedGrams: null, Servings: null } or { CookedGrams: not null, Servings: not null } =>
            "A recipe needs either its cooked weight or how many servings it makes.",
        { CookedGrams: <= 0 or > MaxCookedGrams } => $"A cooked weight must be above 0 and at most {MaxCookedGrams}.",
        { Servings: <= 0 or > MaxServings } => $"Servings must be above 0 and at most {MaxServings}.",
        { Ingredients: null or { Count: 0 } } => "A recipe needs at least one ingredient.",
        { Ingredients.Count: > MaxIngredients } => $"A recipe takes at most {MaxIngredients} ingredients.",
        _ when request.Ingredients.Any(i => i.Amount is <= 0 or > MaxAmount) =>
            $"An ingredient's amount must be above 0 and at most {MaxAmount}.",
        _ => null
    };
}