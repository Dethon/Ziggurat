using System.Net;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

// A scanned barcode, ported from the standalone app as it was.
public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder api) =>
        api.MapGet("/products/{barcode}", FindProductAsync);

    private static async Task<IResult> FindProductAsync(
        string barcode, OpenFoodFactsClient catalog, SugaryDbContext db, CancellationToken ct)
    {
        if (!Barcodes.IsValid(barcode))
        {
            return Results.Problem("That is not a product barcode.", statusCode: StatusCodes.Status400BadRequest);
        }

        // What she saved herself comes first: it is the label she read, it needs no network, and
        // the crowd-sourced database is sometimes wrong.
        var own = await db.Foods.AsNoTracking()
            .Where(f => f.Barcode == barcode && f.CarbsPer100g != null)
            .OrderByDescending(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (own is not null)
        {
            return Results.Ok(
                new PackagedFood(own.Name, own.CarbsPer100g!.Value, own.PortionGrams, own.Unit, null, own.Id));
        }

        try
        {
            return await catalog.FindAsync(barcode, ct) is { } food ? Results.Ok(food) : Results.NotFound();
        }
        // Open Food Facts limits how often it is asked; that passes in moments, unlike an outage.
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return Results.Problem(
                "The product database is busy. Try again in a moment, or take a photo of the nutrition table.",
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Results.Problem(
                "The product database could not be reached.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}