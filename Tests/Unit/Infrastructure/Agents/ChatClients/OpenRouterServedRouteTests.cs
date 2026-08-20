using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// Which model and which provider actually answered is not what was configured: routing picks an
// endpoint per request, and a behavioural failure that cannot be reproduced is diagnosed from the
// route it ran on. It rides the same tee the cost and cache counters do, because it arrives in the
// same place — on the stream, in fields the typed response drops.
public class OpenRouterServedRouteTests
{
    [Fact]
    public void ARoute_IsReadOffAChatCompletionsChunk()
    {
        var route = OpenRouterHttpHelpers.ExtractRouteFromSseData(
            """{"id":"gen-1","provider":"Fireworks","model":"openai/gpt-5.6-luna","choices":[]}""");

        route.ShouldNotBeNull();
        route.Model.ShouldBe("openai/gpt-5.6-luna");
        route.Provider.ShouldBe("Fireworks");
    }

    [Fact]
    public void ARoute_IsReadOffAResponsesEvent()
    {
        var route = OpenRouterHttpHelpers.ExtractRouteFromSseData(
            """
            {"type":"response.completed","response":{"provider":"DeepInfra","model":"z-ai/glm-5.2"}}
            """);

        route.ShouldNotBeNull();
        route.Model.ShouldBe("z-ai/glm-5.2");
        route.Provider.ShouldBe("DeepInfra");
    }

    [Fact]
    public void AChunkNamingNeither_IsNotARoute()
    {
        OpenRouterHttpHelpers.ExtractRouteFromSseData(
            """{"type":"response.output_text.delta","delta":"hola"}""").ShouldBeNull();
    }

    [Fact]
    public void AProviderlessChunk_StillNamesTheModel()
    {
        // Not every wire reports the provider, and the model alone still separates a routing
        // surprise from a prompt defect.
        var route = OpenRouterHttpHelpers.ExtractRouteFromSseData(
            """{"id":"gen-2","model":"openai/gpt-5.6-luna","choices":[]}""");

        route.ShouldNotBeNull();
        route.Model.ShouldBe("openai/gpt-5.6-luna");
        route.Provider.ShouldBeNull();
    }

    [Fact]
    public void AGenerationId_IsCarriedForTheWireThatNamesNoProvider()
    {
        // The Responses wire reports no provider at all, so the id is the only handle the name can
        // be recovered from — by a caller willing to pay a request for it, which no turn is.
        var route = OpenRouterHttpHelpers.ExtractRouteFromSseData(
            """
            {"type":"response.completed","response":{"id":"gen-123-abc","model":"openai/gpt-5.6-luna"}}
            """);

        route.ShouldNotBeNull();
        route.GenerationId.ShouldBe("gen-123-abc");
        route.Provider.ShouldBeNull();
    }

    [Fact]
    public void AnIdThatIsNotAGeneration_IsIgnored()
    {
        // Every event on the Responses wire carries some id; only OpenRouter's own generation id
        // can be looked up, and storing a message id under that name would send the lookup
        // hunting for something that never existed.
        OpenRouterHttpHelpers.ExtractRouteFromSseData(
            """{"type":"response.output_item.added","item":{"id":"msg_tmp_1"}}""").ShouldBeNull();
    }

    [Fact]
    public void MalformedData_IsNotARoute()
    {
        OpenRouterHttpHelpers.ExtractRouteFromSseData("not json at all").ShouldBeNull();
    }
}