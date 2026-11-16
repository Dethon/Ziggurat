using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.RealEstate;
using Infrastructure.Clients;
using Infrastructure.Utils;
using Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerIdealista.McpTools;

// Searched on the calling household's own Idealista account, read through the call's household
// (call.Household): the deployment has no account of its own, so a household that set no keys
// searches nothing rather than on somebody else's.
[McpServerToolType]
public class McpPropertySearchTool(IdealistaClients accounts) : PropertySearchTool
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> call,
        [Description("Country: es (Spain), it (Italy), or pt (Portugal)")]
        IdealistaCountry country,
        [Description("Operation type")]
        IdealistaOperation operation,
        [Description("Property type")]
        IdealistaPropertyType propertyType,
        [Description(
            "Idealista location ID (e.g., '0-EU-ES-28' for Madrid province). Either locationId OR center+distance is required.")]
        string? locationId = null,
        [Description(
            "Geographic coordinates as 'latitude,longitude' (e.g., '40.416775,-3.703790' for Madrid center). Use with distance parameter.")]
        string? center = null,
        [Description("Search radius in meters from center point (e.g., 5000 for 5km). Required when using center.")]
        double? distance = null,
        [Description("Maximum results per page (1-50, default: 20)")]
        int? maxItems = 20,
        [Description("Page number for pagination (starts at 1)")]
        int? numPage = 1,
        [Description("Minimum price in euros")]
        double? minPrice = null,
        [Description("Maximum price in euros")]
        double? maxPrice = null,
        [Description("Minimum size in square meters")]
        double? minSize = null,
        [Description("Maximum size in square meters")]
        double? maxSize = null,
        [Description("Number of bedrooms as comma-separated values (e.g., '2,3' for 2 or 3 bedrooms). Use '4' for 4+.")]
        string? bedrooms = null,
        [Description("Number of bathrooms as comma-separated values (e.g., '1,2'). Use '3' for 3+.")]
        string? bathrooms = null,
        [Description("Sort field")]
        IdealistaSortField? order = null,
        [Description("Sort direction")]
        IdealistaSortDirection? sort = null,
        [Description("Filter for properties with elevator")]
        bool? elevator = null,
        [Description("Filter for properties with garage")]
        bool? garage = null,
        [Description("Filter for properties with terrace")]
        bool? terrace = null,
        [Description("Filter for properties with swimming pool")]
        bool? swimmingPool = null,
        [Description("Filter for properties with air conditioning")]
        bool? airConditioning = null,
        [Description("Filter for new development properties only")]
        bool? newDevelopment = null,
        [Description("Property condition")]
        IdealistaPreservation? preservation = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(locationId) && string.IsNullOrEmpty(center))
        {
            return ToolResponse.Create(new ArgumentException(
                "Either 'locationId' or 'center' with 'distance' must be provided."));
        }

        if (!string.IsNullOrEmpty(center) && !distance.HasValue)
        {
            return ToolResponse.Create(new ArgumentException(
                "When using 'center', 'distance' parameter is required."));
        }

        if (call.Household is not { } household)
        {
            return ToolResponse.Create(ToolError.Create(ToolError.Codes.PermissionDenied,
                "This search acts for no household, and Idealista is searched on a household's own account."));
        }

        if (await household.IdealistaKeysAsync(ct) is not { } keys)
        {
            return ToolResponse.Create(ToolError.Create(ToolError.Codes.Authentication,
                "This household has set no Idealista key, so there is no account to search on. An owner of the household adds one."));
        }

        var result = await RunAsync(
            accounts.For(keys),
            country,
            operation,
            propertyType,
            locationId,
            center,
            distance,
            maxItems,
            numPage,
            minPrice,
            maxPrice,
            minSize,
            maxSize,
            bedrooms,
            bathrooms,
            order,
            sort,
            elevator,
            garage,
            terrace,
            swimmingPool,
            airConditioning,
            newDevelopment,
            preservation,
            ct);

        return ToolResponse.Create(result);
    }
}