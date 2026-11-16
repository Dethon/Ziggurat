using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.RealEstate;

// The search itself, made on whichever account the caller hands it: a household's own (the
// Idealista server's tool, through the household's keys).
public class PropertySearchTool
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    protected const string Name = "property_search";

    protected const string Description =
        """
        Searches for real estate properties on Idealista (Spain, Italy, Portugal).
        Supports filtering by property type (homes, offices, premises, garages, bedrooms),
        operation (sale, rent), location, price range, size, and many property-specific features.
        Returns comprehensive property data for market analysis including price, location, size,
        rooms, description, condition, age, and URLs.
        """;

    protected static async Task<JsonNode> RunAsync(
        IIdealistaClient idealistaClient,
        IdealistaCountry country,
        IdealistaOperation operation,
        IdealistaPropertyType propertyType,
        string? locationId,
        string? center,
        double? distance,
        int? maxItems,
        int? numPage,
        double? minPrice,
        double? maxPrice,
        double? minSize,
        double? maxSize,
        string? bedrooms,
        string? bathrooms,
        IdealistaSortField? order,
        IdealistaSortDirection? sort,
        bool? elevator,
        bool? garage,
        bool? terrace,
        bool? swimmingPool,
        bool? airConditioning,
        bool? newDevelopment,
        IdealistaPreservation? preservation,
        CancellationToken ct)
    {
        var query = new IdealistaSearchQuery
        {
            Country = country,
            Operation = operation,
            PropertyType = propertyType,
            LocationId = locationId,
            Center = center,
            Distance = distance,
            MaxItems = maxItems ?? 20,
            NumPage = numPage ?? 1,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            MinSize = minSize,
            MaxSize = maxSize,
            Bedrooms = bedrooms,
            Bathrooms = bathrooms,
            Order = order,
            Sort = sort,
            Elevator = elevator,
            Garage = garage,
            Terrace = terrace,
            SwimmingPool = swimmingPool,
            AirConditioning = airConditioning,
            NewDevelopment = newDevelopment,
            Preservation = preservation
        };

        var result = await idealistaClient.SearchAsync(query, ct);

        if (result.ElementList.Count == 0)
        {
            return new JsonObject
            {
                ["status"] = "no_results",
                ["total"] = 0,
                ["properties"] = new JsonArray(),
                ["suggestion"] = "No properties found. Try adjusting your search filters or location."
            };
        }

        var properties = result.ElementList
            .Select(x => JsonSerializer.SerializeToNode(x, _jsonOptions))
            .Where(x => x != null)
            .Select(x => x!)
            .ToArray();

        return new JsonObject
        {
            ["status"] = "success",
            ["total"] = result.Total,
            ["totalPages"] = result.TotalPages,
            ["currentPage"] = result.ActualPage,
            ["itemsPerPage"] = result.ItemsPerPage,
            ["summary"] = new JsonArray(result.Summary.Select(s => JsonValue.Create(s)).ToArray()),
            ["properties"] = new JsonArray(properties)
        };
    }
}