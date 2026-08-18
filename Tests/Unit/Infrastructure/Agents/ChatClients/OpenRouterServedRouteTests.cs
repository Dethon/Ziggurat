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
    public void ARouteIsReadOffAChatCompletionsChunk()
    {
        var route = OpenRouterHttpHelpers.ExtractRouteFromSseData(
            """{"id":"gen-1","provider":"Fireworks","model":"openai/gpt-5.6-luna","choices":[]}""");

        route.ShouldNotBeNull();
        route.Model.ShouldBe("openai/gpt-5.6-luna");
        route.Provider.ShouldBe("Fireworks");
    }

    [Fact]
    public void ARouteIsReadOffAResponsesEvent()
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
    public void AChunkNamingNeitherIsNotARoute()
    {
        OpenRouterHttpHelpers.ExtractRouteFromSseData(
            """{"type":"response.output_text.delta","delta":"hola"}""").ShouldBeNull();
    }

    [Fact]
    public void AProviderlessChunkStillNamesTheModel()
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
    public void MalformedDataIsNotARoute()
    {
        OpenRouterHttpHelpers.ExtractRouteFromSseData("not json at all").ShouldBeNull();
    }
}