using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

// The catalogue is the one place a channel learns what a model accepts, for the agent's own
// model and for every model a person is allowed to switch to. Nobody maintains that list by hand.
public class AgentCatalogBuilderTests
{
    private static readonly AgentDefinition _agent = new()
    {
        Id = "jonas",
        Name = "Jonas",
        Description = "general",
        Model = "openai/gpt-5.6-luna",
        ReasoningEffort = "low",
        McpServerEndpoints = []
    };

    [Fact]
    public void TheCatalogue_CarriesWhatTheDefaultModelAcceptsAndWhatEachPatchableModelAccepts()
    {
        var capabilities = new StubCapabilities(new Dictionary<string, IReadOnlyList<AttachmentKind>>
        {
            ["openai/gpt-5.6-luna"] = [AttachmentKind.Image, AttachmentKind.Document],
            ["z-ai/glm-5.2"] = []
        });

        var catalogue = AgentCatalogBuilder.Build(
            [_agent],
            [new PatchableModel("openai/gpt-5.6-luna", "GPT"), new PatchableModel("z-ai/glm-5.2", "GLM")],
            capabilities,
            FixedLemonadeModelSource.None);

        var entry = catalogue.ShouldHaveSingleItem();
        entry.DefaultModelAttachmentKinds.ShouldBe([AttachmentKind.Image, AttachmentKind.Document]);
        entry.PatchableModels.ShouldNotBeNull();
        entry.PatchableModels!.First(m => m.Id == "z-ai/glm-5.2").AcceptedAttachmentKinds.ShouldBeEmpty();
        entry.PatchableModels!.First(m => m.Id == "openai/gpt-5.6-luna").AcceptedAttachmentKinds
            .ShouldBe([AttachmentKind.Image, AttachmentKind.Document]);
    }

    private sealed class StubCapabilities(IReadOnlyDictionary<string, IReadOnlyList<AttachmentKind>> known)
        : IModelCapabilityCatalog
    {
        public IReadOnlyList<AttachmentKind> GetAcceptedAttachmentKinds(string modelId) =>
            known.TryGetValue(modelId, out var kinds) ? kinds : AttachmentKinds.All;
    }

    private static readonly AgentDefinition _jack = _agent with { Id = "jack", Name = "Jack" };

    private static readonly StubCapabilities _permissive = new(new Dictionary<string, IReadOnlyList<AttachmentKind>>());

    private static readonly PatchableModel _configured = new("openai/gpt-5.6-luna", "GPT");

    // A Lemonade model is a patchable model like any other, offered to every agent, after the
    // configured ones, and named as the host's so nothing downstream mistakes it for a hosted one.
    [Fact]
    public void LemonadeModels_AreAppendedToEveryAgent_WithNamespacedIds()
    {
        var lemonade = new FixedLemonadeModelSource([
            new LemonadeModel("Qwen3.8-27B-GGUF-UD-Q4_K_XL", [AttachmentKind.Image], 50000),
            new LemonadeModel("GLM-4.7-Flash-GGUF", [], 32768)
        ]);

        var catalogue = AgentCatalogBuilder.Build([_agent, _jack], [_configured], _permissive, lemonade);

        catalogue.Count.ShouldBe(2);
        catalogue.ShouldAllBe(entry => entry.PatchableModels!.Select(m => m.Id).SequenceEqual(new[]
        {
            "openai/gpt-5.6-luna", "lemonade/Qwen3.8-27B-GGUF-UD-Q4_K_XL", "lemonade/GLM-4.7-Flash-GGUF"
        }));
    }

    // The display name is the id up to its first -GGUF: the quantization suffix is what a person
    // would have to read past to find the model. The id itself is never touched.
    [Fact]
    public void ALemonadeModel_IsShownByItsIdTrimmedAtTheFirstGguf()
    {
        var lemonade = new FixedLemonadeModelSource([
            new LemonadeModel("Qwen3.8-27B-GGUF-UD-Q4_K_XL", [], null)
        ]);

        var catalogue = AgentCatalogBuilder.Build([_agent], [], _permissive, lemonade);

        var model = catalogue.Single().PatchableModels!.Single();
        model.Name.ShouldBe("Qwen3.8-27B");
        model.Id.ShouldBe("lemonade/Qwen3.8-27B-GGUF-UD-Q4_K_XL");
    }

    // Two quantizations of one model trim to one name, and a person picking between them must
    // never be shown two identical entries: both fall back to their full ids.
    [Fact]
    public void TwoLemonadeModelsThatTrimToOneName_AreBothShownByTheirFullIds()
    {
        var lemonade = new FixedLemonadeModelSource([
            new LemonadeModel("Qwen3.8-27B-GGUF-UD-Q4_K_XL", [], null),
            new LemonadeModel("Qwen3.8-27B-GGUF-Q8_0", [], null),
            new LemonadeModel("Gemma-4-12B-it-GGUF", [], null)
        ]);

        var catalogue = AgentCatalogBuilder.Build([_agent], [], _permissive, lemonade);

        catalogue.Single().PatchableModels!.Select(m => m.Name)
            .ShouldBe(["Qwen3.8-27B-GGUF-UD-Q4_K_XL", "Qwen3.8-27B-GGUF-Q8_0", "Gemma-4-12B-it"]);
    }

    // What a Lemonade model accepts is what the host said, not what the hosted provider's
    // catalogue would answer for an id it has never heard of.
    [Fact]
    public void ALemonadeModel_CarriesTheAttachmentKindsTheHostReported()
    {
        var lemonade = new FixedLemonadeModelSource([
            new LemonadeModel("sees", [AttachmentKind.Image], null),
            new LemonadeModel("reads", [], null)
        ]);

        var catalogue = AgentCatalogBuilder.Build([_agent], [], _permissive, lemonade);

        var models = catalogue.Single().PatchableModels!;
        models.Single(m => m.Id == "lemonade/sees").AcceptedAttachmentKinds.ShouldBe([AttachmentKind.Image]);
        models.Single(m => m.Id == "lemonade/reads").AcceptedAttachmentKinds.ShouldBeEmpty();
    }

    [Fact]
    public void ASourceOfferingNothing_AddsNothing()
    {
        var catalogue = AgentCatalogBuilder.Build(
            [_agent], [_configured], _permissive, FixedLemonadeModelSource.None);

        catalogue.Single().PatchableModels!.Select(m => m.Id).ShouldBe(["openai/gpt-5.6-luna"]);
    }
}