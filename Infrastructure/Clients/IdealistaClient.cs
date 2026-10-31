using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Infrastructure.Clients;

// One Idealista account's searches: its OAuth token is kept for as long as Idealista says it lasts.
// Each request takes a client from the factory, so an instance kept per account (IdealistaClients)
// never pins one handler past its life.
public class IdealistaClient(Func<HttpClient> http, string apiKey, string apiSecret) : IIdealistaClient
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public async Task<IdealistaSearchResult> SearchAsync(IdealistaSearchQuery query, CancellationToken ct = default)
    {
        await EnsureValidTokenAsync(ct);

        var url = $"3.5/{ToWire(query.Country)}/search";
        var content = BuildSearchContent(query);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = content;

        var response = await http().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var idealistaResponse = await response.Content.ReadFromJsonAsync<IdealistaApiResponse>(ct)
                                ?? throw new InvalidOperationException("Failed to deserialize Idealista response");

        return MapToSearchResult(idealistaResponse);
    }

    private async Task EnsureValidTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
        {
            return;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return;
            }

            await RefreshTokenAsync(ct);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, "oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "read"
        });

        var response = await http().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var tokenResponse = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(ct)
                            ?? throw new InvalidOperationException("Failed to get OAuth token");

        _accessToken = tokenResponse.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
    }

    private static FormUrlEncodedContent BuildSearchContent(IdealistaSearchQuery query)
    {
        var parameters = new Dictionary<string, string>
        {
            ["country"] = ToWire(query.Country),
            ["operation"] = ToWire(query.Operation),
            ["propertyType"] = ToWire(query.PropertyType)
        };

        AddIfNotNull(parameters, "center", query.Center);
        AddIfNotNull(parameters, "distance", query.Distance?.ToString());
        AddIfNotNull(parameters, "locationId", query.LocationId);
        AddIfNotNull(parameters, "locale", query.Locale);
        AddIfNotNull(parameters, "maxItems", query.MaxItems?.ToString());
        AddIfNotNull(parameters, "numPage", query.NumPage?.ToString());
        AddIfNotNull(parameters, "maxPrice", query.MaxPrice?.ToString());
        AddIfNotNull(parameters, "minPrice", query.MinPrice?.ToString());
        AddIfNotNull(parameters, "sinceDate", query.SinceDate);
        AddIfNotNull(parameters, "order", query.Order is { } o ? ToCamelWire(o) : null);
        AddIfNotNull(parameters, "sort", query.Sort is { } s ? ToWire(s) : null);
        AddIfNotNull(parameters, "hasMultimedia", query.HasMultimedia?.ToString().ToLowerInvariant());

        AddIfNotNull(parameters, "minSize", query.MinSize?.ToString());
        AddIfNotNull(parameters, "maxSize", query.MaxSize?.ToString());

        // Home specific
        AddIfTrue(parameters, "flat", query.Flat);
        AddIfTrue(parameters, "penthouse", query.Penthouse);
        AddIfTrue(parameters, "duplex", query.Duplex);
        AddIfTrue(parameters, "studio", query.Studio);
        AddIfTrue(parameters, "chalet", query.Chalet);
        AddIfTrue(parameters, "countryHouse", query.CountryHouse);
        AddIfNotNull(parameters, "bedrooms", query.Bedrooms);
        AddIfNotNull(parameters, "bathrooms", query.Bathrooms);
        AddIfNotNull(parameters, "preservation", query.Preservation is { } p ? ToWire(p) : null);
        AddIfTrue(parameters, "newDevelopment", query.NewDevelopment);
        AddIfNotNull(parameters, "furnished", query.Furnished);
        AddIfTrue(parameters, "garage", query.Garage);
        AddIfTrue(parameters, "terrace", query.Terrace);
        AddIfTrue(parameters, "exterior", query.Exterior);
        AddIfTrue(parameters, "elevator", query.Elevator);
        AddIfTrue(parameters, "swimmingPool", query.SwimmingPool);
        AddIfTrue(parameters, "airConditioning", query.AirConditioning);
        AddIfTrue(parameters, "storeRoom", query.StoreRoom);
        AddIfTrue(parameters, "builtinWardrobes", query.BuiltinWardrobes);
        AddIfNotNull(parameters, "subTypology", query.SubTypology);

        // Garage specific
        AddIfTrue(parameters, "automaticDoor", query.AutomaticDoor);
        AddIfTrue(parameters, "motorcycleParking", query.MotorcycleParking);
        AddIfTrue(parameters, "security", query.Security);

        // Premise specific
        AddIfNotNull(parameters, "location", query.Location);
        AddIfTrue(parameters, "corner", query.Corner);
        AddIfTrue(parameters, "smokeVentilation", query.SmokeVentilation);
        AddIfTrue(parameters, "heating", query.Heating);
        AddIfTrue(parameters, "transfer", query.Transfer);
        AddIfNotNull(parameters, "buildingTypes", query.BuildingTypes);

        // Office specific
        AddIfNotNull(parameters, "layout", query.Layout);
        AddIfNotNull(parameters, "buildingType", query.BuildingType);
        AddIfTrue(parameters, "hotWater", query.HotWater);

        // Room specific
        AddIfNotNull(parameters, "housemates", query.Housemates);
        AddIfNotNull(parameters, "smokePolicy", query.SmokePolicy);
        AddIfTrue(parameters, "petsPolicy", query.PetsPolicy);
        AddIfTrue(parameters, "gayPartners", query.GayPartners);
        AddIfNotNull(parameters, "newGender", query.NewGender);

        AddIfTrue(parameters, "bankOffer", query.BankOffer);
        AddIfTrue(parameters, "virtualTour", query.VirtualTour);

        return new FormUrlEncodedContent(parameters);
    }

    private static string ToWire<T>(T value) where T : struct, Enum
        => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string ToCamelWire<T>(T value) where T : struct, Enum
        => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static void AddIfNotNull(Dictionary<string, string> parameters, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            parameters[key] = value;
        }
    }

    private static void AddIfTrue(Dictionary<string, string> parameters, string key, bool? value)
    {
        if (value == true)
        {
            parameters[key] = "true";
        }
    }

    private static IdealistaSearchResult MapToSearchResult(IdealistaApiResponse response)
    {
        var properties = response.ElementList?.Select(e => new IdealistaProperty
        {
            PropertyCode = e.PropertyCode ?? string.Empty,
            Address = e.Address,
            District = e.District,
            Municipality = e.Municipality,
            Province = e.Province,
            Neighborhood = e.Neighborhood,
            Region = e.Region,
            Subregion = e.Subregion,
            Country = e.Country,
            Latitude = e.Latitude,
            Longitude = e.Longitude,
            Distance = e.Distance,
            Url = e.Url,
            Thumbnail = e.Thumbnail,
            NumPhotos = e.NumPhotos,
            HasVideo = e.HasVideo,
            Operation = e.Operation ?? "unknown",
            Price = e.Price ?? 0,
            PriceByArea = e.PriceByArea,
            PropertyType = e.PropertyType,
            Size = e.Size,
            Rooms = e.Rooms,
            Bathrooms = e.Bathrooms,
            Floor = e.Floor,
            Exterior = e.Exterior,
            HasLift = e.HasLift,
            ShowAddress = e.ShowAddress,
            Status = e.Status,
            Condition = e.Condition,
            Age = e.Age,
            Description = e.Description,
            NewDevelopment = e.NewDevelopment,
            NewDevelopmentFinished = e.NewDevelopmentFinished,
            NewProperty = e.NewProperty,
            Agency = e.Agency,
            ParkingSpace = e.ParkingSpace != null
                ? new IdealistaParkingSpace
                {
                    HasParkingSpace = e.ParkingSpace.HasParkingSpace,
                    IsParkingSpaceIncludedInPrice = e.ParkingSpace.IsParkingSpaceIncludedInPrice,
                    ParkingSpacePrice = e.ParkingSpace.ParkingSpacePrice
                }
                : null,
            DetailedType = e.DetailedType != null
                ? new IdealistaDetailedType
                {
                    Typology = e.DetailedType.Typology,
                    Subtypology = e.DetailedType.Subtypology
                }
                : null,
            ExternalReference = e.ExternalReference,
            GarageType = e.GarageType,
            TenantGender = e.TenantGender,
            IsSmokingAllowed = e.IsSmokingAllowed
        }).ToList() ?? [];

        return new IdealistaSearchResult
        {
            ActualPage = response.ActualPage ?? 1,
            ItemsPerPage = response.ItemsPerPage ?? 0,
            Total = response.Total ?? 0,
            TotalPages = response.TotalPages ?? 0,
            Paginable = response.Paginable ?? false,
            Summary = response.Summary ?? [],
            ElementList = properties
        };
    }

    private record OAuthTokenResponse
    {
        [JsonPropertyName("access_token")] public required string AccessToken { get; init; }

        [JsonPropertyName("token_type")] public string? TokenType { get; init; }

        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }

        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }

    [PublicAPI]
    private record IdealistaApiResponse
    {
        [JsonPropertyName("actualPage")] public int? ActualPage { get; init; }

        [JsonPropertyName("itemsPerPage")] public int? ItemsPerPage { get; init; }

        [JsonPropertyName("total")] public int? Total { get; init; }

        [JsonPropertyName("totalPages")] public int? TotalPages { get; init; }

        [JsonPropertyName("paginable")] public bool? Paginable { get; init; }

        [JsonPropertyName("summary")] public List<string>? Summary { get; init; }

        [JsonPropertyName("elementList")] public List<IdealistaApiElement>? ElementList { get; init; }
    }

    [PublicAPI]
    private record IdealistaApiElement
    {
        [JsonPropertyName("propertyCode")] public string? PropertyCode { get; init; }

        [JsonPropertyName("address")] public string? Address { get; init; }

        [JsonPropertyName("district")] public string? District { get; init; }

        [JsonPropertyName("municipality")] public string? Municipality { get; init; }

        [JsonPropertyName("province")] public string? Province { get; init; }

        [JsonPropertyName("neighborhood")] public string? Neighborhood { get; init; }

        [JsonPropertyName("region")] public string? Region { get; init; }

        [JsonPropertyName("subregion")] public string? Subregion { get; init; }

        [JsonPropertyName("country")] public string? Country { get; init; }

        [JsonPropertyName("latitude")] public double? Latitude { get; init; }

        [JsonPropertyName("longitude")] public double? Longitude { get; init; }

        [JsonPropertyName("distance")] public string? Distance { get; init; }

        [JsonPropertyName("url")] public string? Url { get; init; }

        [JsonPropertyName("thumbnail")] public string? Thumbnail { get; init; }

        [JsonPropertyName("numPhotos")] public int? NumPhotos { get; init; }

        [JsonPropertyName("hasVideo")] public bool? HasVideo { get; init; }

        [JsonPropertyName("operation")] public string? Operation { get; init; }

        [JsonPropertyName("price")] public double? Price { get; init; }

        [JsonPropertyName("priceByArea")] public double? PriceByArea { get; init; }

        [JsonPropertyName("propertyType")] public string? PropertyType { get; init; }

        [JsonPropertyName("size")] public double? Size { get; init; }

        [JsonPropertyName("rooms")] public int? Rooms { get; init; }

        [JsonPropertyName("bathrooms")] public int? Bathrooms { get; init; }

        [JsonPropertyName("floor")] public string? Floor { get; init; }

        [JsonPropertyName("exterior")] public bool? Exterior { get; init; }

        [JsonPropertyName("hasLift")] public bool? HasLift { get; init; }

        [JsonPropertyName("showAddress")] public bool? ShowAddress { get; init; }

        [JsonPropertyName("status")] public string? Status { get; init; }

        [JsonPropertyName("condition")] public string? Condition { get; init; }

        [JsonPropertyName("age")] public string? Age { get; init; }

        [JsonPropertyName("description")] public string? Description { get; init; }

        [JsonPropertyName("newDevelopment")] public bool? NewDevelopment { get; init; }

        [JsonPropertyName("newDevelopmentFinished")] public bool? NewDevelopmentFinished { get; init; }

        [JsonPropertyName("newProperty")] public bool? NewProperty { get; init; }

        [JsonPropertyName("agency")] public bool? Agency { get; init; }

        [JsonPropertyName("parkingSpace")] public IdealistaApiParkingSpace? ParkingSpace { get; init; }

        [JsonPropertyName("detailedType")] public IdealistaApiDetailedType? DetailedType { get; init; }

        [JsonPropertyName("externalReference")]
        public string? ExternalReference { get; init; }

        [JsonPropertyName("garageType")] public string? GarageType { get; init; }

        [JsonPropertyName("tenantGender")] public string? TenantGender { get; init; }

        [JsonPropertyName("isSmokingAllowed")] public bool? IsSmokingAllowed { get; init; }
    }

    [PublicAPI]
    private record IdealistaApiParkingSpace
    {
        [JsonPropertyName("hasParkingSpace")] public bool? HasParkingSpace { get; init; }

        [JsonPropertyName("isParkingSpaceIncludedInPrice")]
        public bool? IsParkingSpaceIncludedInPrice { get; init; }

        [JsonPropertyName("parkingSpacePrice")]
        public double? ParkingSpacePrice { get; init; }
    }

    [PublicAPI]
    private record IdealistaApiDetailedType
    {
        [JsonPropertyName("typology")] public string? Typology { get; init; }

        [JsonPropertyName("subtypology")] public string? Subtypology { get; init; }
    }
}