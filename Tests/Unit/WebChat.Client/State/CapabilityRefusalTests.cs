using Domain.DTOs.Channel;
using Shouldly;
using WebChat.Client.Components;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Composer;

namespace Tests.Unit.WebChat.Client.State;

// Attaching a file to a model that cannot read it costs the person nothing: the send is blocked
// with an explanation naming the model, and the files stay attached, so the fix is switching
// model rather than starting the message again.
public class CapabilityRefusalTests
{
    private const string AgentId = "jack";

    private static readonly IReadOnlyList<AgentCatalogEntry> _agents =
    [
        new AgentCatalogEntry(
            AgentId, "Jack", null,
            DefaultModel: "text/only",
            PatchableModels:
            [
                new PatchableModel("text/only", "Text only", []),
                new PatchableModel("sees/pictures", "Sees pictures", [AttachmentKind.Image])
            ],
            DefaultModelAttachmentKinds: [])
    ];

    private static readonly ComposerAttachment _photo = new()
    {
        LocalId = "local-1",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4,
        Status = AttachmentStatus.Ready,
        Reference = new AttachmentReference
        {
            Id = "7-42/abc", FileName = "photo.png", MediaType = "image/png", SizeBytes = 4
        }
    };

    [Fact]
    public void TheEffectiveModel_IsThePerMessageChoiceFallingBackToTheAgentDefault()
    {
        ComposerSelectors.EffectiveModel(Settings(null), _agents, AgentId).ShouldBe("text/only");
        ComposerSelectors.EffectiveModel(Settings("sees/pictures"), _agents, AgentId)
            .ShouldBe("sees/pictures");
    }

    [Fact]
    public void AModelThatCannotTakeAnAttachedKind_RefusesNamingTheModelAndTheReason()
    {
        var refusal = ComposerSelectors.CapabilityRefusal(Settings(null), _agents, AgentId, [_photo]);

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("text/only");
        refusal.ShouldContain("images");
    }

    [Fact]
    public void SwitchingToACapableModel_ReEnablesSendingWithoutReattaching()
    {
        ComposerSelectors.CapabilityRefusal(Settings("sees/pictures"), _agents, AgentId, [_photo])
            .ShouldBeNull();
    }

    [Fact]
    public void AnAgentWhoseCapabilityIsNotKnown_RefusesNothing()
    {
        IReadOnlyList<AgentCatalogEntry> unknown =
            [new AgentCatalogEntry(AgentId, "Jack", null, DefaultModel: "text/only")];

        ComposerSelectors.CapabilityRefusal(Settings(null), unknown, AgentId, [_photo]).ShouldBeNull();
    }

    [Fact]
    public void AComposerWithNoAttachments_RefusesNothing()
    {
        ComposerSelectors.CapabilityRefusal(Settings(null), _agents, AgentId, []).ShouldBeNull();
    }

    [Fact]
    public void ARefusal_BlocksTheSendEvenWithTextTyped()
    {
        ChatInputLogic.CanSend(
            disabled: false, "look at this", isStreaming: false,
            readyAttachments: 1, uploadInFlight: false,
            capabilityRefusal: "text/only cannot read images.").ShouldBeFalse();
    }

    [Fact]
    public void NoRefusal_AllowsASendWithAttachmentsAndNoText()
    {
        ChatInputLogic.CanSend(
            disabled: false, inputText: "", isStreaming: false,
            readyAttachments: 1).ShouldBeTrue();
    }

    [Fact]
    public void AFileStillUploading_HoldsTheSend()
    {
        ChatInputLogic.CanSend(
            disabled: false, "wait for it", isStreaming: false,
            readyAttachments: 0, uploadInFlight: true).ShouldBeFalse();
    }

    [Fact]
    public void NoTextAndNoAttachments_StillCannotBeSent()
    {
        ChatInputLogic.CanSend(disabled: false, inputText: "", isStreaming: false).ShouldBeFalse();
    }

    private static AgentSettingsState Settings(string? model) => new()
    {
        ByAgent = model is null
            ? new Dictionary<string, AgentModelSettings>()
            : new Dictionary<string, AgentModelSettings> { [AgentId] = new(model, null) }
    };
}