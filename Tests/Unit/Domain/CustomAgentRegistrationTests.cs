using System.Text.Json;
using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain;

// External hosts (SexyTime today) register their agent by POSTing this record to /api/agents,
// so its JSON shape is a wire contract rather than an implementation detail. The minimal API
// deserializes with JsonSerializerDefaults.Web, which reads an enum as a number unless the enum
// itself says otherwise -- and a string is the only spelling a JSON config file upstream can
// produce, so without that the whole routing object arrives as a 400 nobody asked for.
public class CustomAgentRegistrationTests
{
    // The bare-number threshold shorthand is a configuration-binder affordance, not a wire one:
    // a caller sending JSON sends the object form, which is what serializing the bound object
    // produces anyway.
    [Fact]
    public void Deserialize_ProviderRoutingWithThresholds_BindsEveryCutoff()
    {
        var registration = Deserialize(
            """
            {
                "name": "SexyTime",
                "model": "z-ai/glm-5.1",
                "mcpServerEndpoints": [],
                "providerRouting": {
                    "sort": "latency",
                    "preferredMinThroughput": { "p50": 80 },
                    "maxPrice": { "prompt": 1, "completion": 2 }
                }
            }
            """);

        var routing = registration.ProviderRouting.ShouldNotBeNull();
        routing.PreferredMinThroughput!.P50.ShouldBe(80);
        routing.MaxPrice!.Prompt.ShouldBe(1);
        routing.MaxPrice.Completion.ShouldBe(2);
    }

    // The body an external host actually posts, rather than the tidy one a hand-written example
    // would use: SexyTime's /api/config serializes its config record with default options, so
    // every unset field arrives as an explicit null rather than being omitted.
    [Fact]
    public void Deserialize_ProviderRoutingWithExplicitNulls_BindsWhatIsSet()
    {
        var registration = Deserialize(
            """
            {
                "name": "SexyTime",
                "model": "z-ai/glm-5.1",
                "mcpServerEndpoints": [],
                "providerRouting": {
                    "sort": "throughput",
                    "order": null,
                    "only": null,
                    "ignore": null,
                    "allowFallbacks": null,
                    "preferredMinThroughput": null,
                    "preferredMaxLatency": null,
                    "maxPrice": null
                }
            }
            """);

        var routing = registration.ProviderRouting.ShouldNotBeNull();
        routing.Sort.ShouldBe(ProviderSort.Throughput);
        routing.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Deserialize_UnknownSort_Throws()
    {
        Should.Throw<JsonException>(() => Deserialize(
            """
            {
                "name": "SexyTime",
                "model": "z-ai/glm-5.1",
                "mcpServerEndpoints": [],
                "providerRouting": { "sort": "cheapest" }
            }
            """));
    }

    // Every registration sent before this field existed omits it, and omission has to keep
    // meaning balanced routing rather than an empty `provider` object.
    [Fact]
    public void Deserialize_NoProviderRouting_LeavesItNull()
    {
        var registration = Deserialize(
            """
            {
                "name": "SexyTime",
                "model": "z-ai/glm-5.1",
                "mcpServerEndpoints": []
            }
            """);

        registration.ProviderRouting.ShouldBeNull();
    }

    private static CustomAgentRegistration Deserialize(string json)
    {
        return JsonSerializer.Deserialize<CustomAgentRegistration>(json, JsonSerializerOptions.Web)!;
    }
}