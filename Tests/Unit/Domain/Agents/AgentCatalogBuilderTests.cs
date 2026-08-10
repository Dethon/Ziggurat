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
            capabilities);

        var entry = catalogue.ShouldHaveSingleItem();
        entry.DefaultModelAttachmentKinds.ShouldBe([AttachmentKind.Image, AttachmentKind.Document]);
        entry.PatchableModels.ShouldNotBeNull();
        entry.PatchableModels!.First(m => m.Id == "z-ai/glm-5.2").AcceptedAttachmentKinds.ShouldBeEmpty();
        entry.PatchableModels!.First(m => m.Id == "openai/gpt-5.6-luna").AcceptedAttachmentKinds
            .ShouldBe([AttachmentKind.Image, AttachmentKind.Document]);
    }

    [Fact]
    public void AModelTheProviderNeverDescribed_IsCarriedAsPermissive()
    {
        var catalogue = AgentCatalogBuilder.Build(
            [_agent],
            [new PatchableModel("some/unknown", "Unknown")],
            new StubCapabilities(new Dictionary<string, IReadOnlyList<AttachmentKind>>()));

        catalogue[0].PatchableModels![0].AcceptedAttachmentKinds.ShouldBe(AttachmentKinds.All, ignoreOrder: true);
        catalogue[0].DefaultModelAttachmentKinds.ShouldBe(AttachmentKinds.All, ignoreOrder: true);
    }

    private sealed class StubCapabilities(IReadOnlyDictionary<string, IReadOnlyList<AttachmentKind>> known)
        : IModelCapabilityCatalog
    {
        public IReadOnlyList<AttachmentKind> GetAcceptedAttachmentKinds(string modelId) =>
            known.TryGetValue(modelId, out var kinds) ? kinds : AttachmentKinds.All;
    }
}