using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpServerSugary.App;

// A packaged product's numbers. FoodId is set when the answer came from her own saved foods rather
// than the product database.
public record PackagedFood(
    string? Name, double CarbsPer100g, double? ServingGrams, string Unit, string? Note, Guid? FoodId = null)
{
    public static string NormalizeUnit(string? unit) =>
        unit?.Trim().ToLowerInvariant() is "ml" or "cl" or "l" ? "ml" : "g";
}

// Open Food Facts, ported from the standalone app as it was.
public class OpenFoodFactsClient(HttpClient http)
{
    private const string Fields =
        "product_name,brands,nutriments,serving_quantity,serving_quantity_unit,product_quantity_unit";

    public async Task<PackagedFood?> FindAsync(string barcode, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/v2/product/{barcode}.json?fields={Fields}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static PackagedFood? Parse(string json)
    {
        var product = JsonNode.Parse(json)?["product"];
        // A product without a carbohydrate value is as good as unknown: reporting it as 0 g
        // would be indistinguishable from a real zero. So is one whose value no food can have, more
        // than its own weight or less than none, as a label read with one is refused.
        if (Number(product?["nutriments"]?["carbohydrates_100g"]) is not (>= 0 and <= 100 and var carbs))
        {
            return null;
        }

        var name = new[] { Text(product!["product_name"]), Text(product["brands"]) }
            .Where(part => part is not null)
            .Distinct();
        var unit = Text(product["serving_quantity_unit"]) ?? Text(product["product_quantity_unit"]);

        return new PackagedFood(
            string.Join(" · ", name) is { Length: > 0 } joined ? joined : null,
            carbs,
            Number(product["serving_quantity"]) is > 0 and var serving ? serving : null,
            PackagedFood.NormalizeUnit(unit),
            null);
    }

    // Open Food Facts is crowd-sourced and serves the same field as a number or a string.
    private static double? Number(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.Number => node.GetValue<double>(),
        JsonValueKind.String when double.TryParse(
            node.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static string? Text(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Trim() is { Length: > 0 } text
            ? text
            : null;
}