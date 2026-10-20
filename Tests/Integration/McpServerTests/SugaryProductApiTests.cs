using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Integration.McpServerTests;

// A scanned barcode: her own saved foods answer first, then Open Food Facts, stubbed here at its
// HTTP boundary so the real client — its parsing and how it reports a busy or broken database — is
// what the app talks to.
public sealed class SugaryProductApiTests : IDisposable
{
    private readonly WireMockServer _openFoodFacts = WireMockServer.Start();

    public void Dispose() => _openFoodFacts.Dispose();

    private void Knows(string barcode, string product) =>
        _openFoodFacts.Given(Request.Create().WithPath($"/api/v2/product/{barcode}.json").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"status":1,"product":{{product}}}"""));

    private void Answers(HttpStatusCode status) =>
        _openFoodFacts.Given(Request.Create().WithPath("/api/v2/product/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode((int)status));

    private Task<SugaryServer> StartAsync() => SugaryServer.StartAsync(openFoodFacts: _openFoodFacts.Url + "/");

    [Fact]
    public async Task AProduct_IsLookedUpByBarcode()
    {
        Knows("8410188012092", """
            {"product_name":"Corn Flakes","brands":"Kellogg's","nutriments":{"carbohydrates_100g":84},
             "serving_quantity":30,"serving_quantity_unit":"g"}
            """);
        _openFoodFacts.Given(Request.Create().WithPath("/api/v2/product/0000000000000.json").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        await using var server = await StartAsync();
        var client = await server.SignedInAsync();

        var product = (await client.GetFromJsonAsync<JsonNode>("/api/products/8410188012092"))!;
        product["name"]!.GetValue<string>().ShouldBe("Corn Flakes · Kellogg's");
        product["carbsPer100g"]!.GetValue<double>().ShouldBe(84);
        product["servingGrams"]!.GetValue<double>().ShouldBe(30);
        product["unit"]!.GetValue<string>().ShouldBe("g");
        (await client.GetAsync("/api/products/0000000000000")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/products/not-a-code")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // Open Food Facts asks every client to identify itself.
    [Fact]
    public async Task TheProductDatabase_IsToldWhoIsAsking()
    {
        Knows("8410188012092", """{"product_name":"Corn Flakes","nutriments":{"carbohydrates_100g":84}}""");
        await using var server = await StartAsync();
        var client = await server.SignedInAsync();

        await client.GetAsync("/api/products/8410188012092");

        _openFoodFacts.LogEntries.Single().RequestMessage!.Headers!["User-Agent"].ShouldContain(agent => agent.Contains("Sugary"));
    }

    // A product with no carbohydrate value is as good as unknown: 0 g would pass for a real zero.
    [Fact]
    public async Task AProductWithoutCarbs_IsNotFound()
    {
        Knows("8410188012092", """{"product_name":"Mystery","nutriments":{}}""");
        await using var server = await StartAsync();
        var client = await server.SignedInAsync();

        (await client.GetAsync("/api/products/8410188012092")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A crowd-sourced value no food can have — more carbohydrate than weight, or less than none — is
    // no value at all, as a label read with one is refused: offered, it would reach her dose.
    [Theory]
    [InlineData("150")]
    [InlineData("-3")]
    public async Task AProductWithCarbsNoFoodCanHave_IsNotFound(string carbs)
    {
        Knows("8410188012092", $$$"""{"product_name":"Corn Flakes","nutriments":{"carbohydrates_100g":{{{carbs}}}}}""");
        await using var server = await StartAsync();
        var client = await server.SignedInAsync();

        (await client.GetAsync("/api/products/8410188012092")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "busy")]
    [InlineData(HttpStatusCode.InternalServerError, "could not be reached")]
    public async Task AProductDatabaseFailure_SaysWhetherItIsWorthTryingAgain(HttpStatusCode upstream, string expected)
    {
        Answers(upstream);
        await using var server = await StartAsync();
        var client = await server.SignedInAsync();

        var response = await client.GetAsync("/api/products/8410188012092");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await response.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>().ShouldContain(expected);
    }

    [Fact]
    public async Task AnUnreachableProductDatabase_IsABadGateway()
    {
        _openFoodFacts.Stop();
        await using var server = await StartAsync();
        var client = await server.SignedInAsync();

        var response = await client.GetAsync("/api/products/8410188012092");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task ABarcodeSheAlreadySaved_AnswersFromHerFoodsWithoutAskingTheDatabase()
    {
        // The database disagrees with the label she photographed; her own entry wins.
        Knows("8480000823298", """
            {"product_name":"Pan de molde","brands":"Hacendado","nutriments":{"carbohydrates_100g":39},
             "serving_quantity":100}
            """);
        await using var server = await StartAsync();
        var client = await server.SignedInAsync();
        var saved = await client.PostAsJsonAsync("/api/foods", new
        {
            name = "Pan de molde",
            carbsPer100g = 46,
            unit = "g",
            portionGrams = 32,
            barcode = "8480000823298",
            source = "label"
        });
        var id = (await saved.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();

        var product = (await client.GetFromJsonAsync<JsonNode>("/api/products/8480000823298"))!;

        product["carbsPer100g"]!.GetValue<double>().ShouldBe(46);
        product["servingGrams"]!.GetValue<double>().ShouldBe(32);
        product["foodId"]!.GetValue<string>().ShouldBe(id);
        _openFoodFacts.LogEntries.ShouldBeEmpty();
    }
}