using Domain.DTOs.Channel;
using Mcp.Hosting;
using Shouldly;
using Telegram.Bot.Types;

namespace Tests.Unit.McpChannelTelegram;

// A model that cannot read what was attached would answer as though nothing had been sent, so this
// refusal is a property of the turn rather than of one file: the whole message stops.
//
// The catalogue arrives through the tool's own entry point, which is the same registration the
// agent already performs on connect and on every reconnect.
public class TelegramBotServiceCapabilityTests : IDisposable
{
    private readonly TelegramPollingHarness _harness = new();

    // The agent is the household's as it runs it: the registered catalogue is the home's, and a
    // household whose jack runs another model is checked against that model, not the home's.
    [Fact]
    public async Task ADocumentForTheModelTheHouseholdsAgentRuns_IsRefusedThoughTheHomesAgentOfThatIdReadsIt()
    {
        Register(
            [new AgentCatalogEntry(
                "jack", "Jack", null, DefaultModel: "sees/everything",
                PatchableModels: [new PatchableModel("pictures-only/model", "Pictures", [AttachmentKind.Image])],
                DefaultModelAttachmentKinds: [AttachmentKind.Image, AttachmentKind.Document])],
            householdModel: "pictures-only/model");
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask summarise this");
        message.Document = TelegramPollingHarness.Document();

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).ShouldBeEmpty();
        _harness.Sent.ShouldHaveSingleItem().Text.ShouldContain("pictures-only/model");
    }

    [Fact]
    public async Task AnImageForAModelThatCannotReadImages_IsRefusedAndNoTurnIsEmitted()
    {
        Register(new AgentCatalogEntry(
            "jack", "Jack", null, DefaultModel: "text-only/model", DefaultModelAttachmentKinds: []));

        await DriveAPhotoAsync();

        (await _harness.ReceiveAsync()).ShouldBeEmpty();
        var reply = _harness.Sent.ShouldHaveSingleItem();
        reply.Text.ShouldContain("text-only/model");
        reply.Text.ShouldContain("images");
    }

    [Fact]
    public async Task ADocumentForAModelThatCannotReadDocuments_IsRefusedAndNoTurnIsEmitted()
    {
        Register(new AgentCatalogEntry(
            "jack", "Jack", null,
            DefaultModel: "pictures-only/model",
            DefaultModelAttachmentKinds: [AttachmentKind.Image]));

        var message = TelegramPollingHarness.MediaMessage(caption: "/ask summarise this");
        message.Document = TelegramPollingHarness.Document();

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).ShouldBeEmpty();
        var reply = _harness.Sent.ShouldHaveSingleItem();
        reply.Text.ShouldContain("pictures-only/model");
        reply.Text.ShouldContain("documents");
    }

    // Permissive wherever the catalogue is silent: a blip at the provider, or a cold start before
    // any agent has connected, must not remove the feature.
    [Fact]
    public async Task WithTheCatalogueSilentAboutTheAgentsModel_AttachmentsGoThrough()
    {
        Register(
            [new AgentCatalogEntry(
                "someone-else", "Other", null,
                DefaultModel: "text-only/model",
                DefaultModelAttachmentKinds: [])],
            householdModel: "unknown/model");

        await DriveAPhotoAsync();

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Attachments.ShouldNotBeNull();
        _harness.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task AttachmentsTheModelAccepts_AreUnaffected()
    {
        Register(new AgentCatalogEntry(
            "jack", "Jack", null,
            DefaultModel: "sees/everything",
            DefaultModelAttachmentKinds: [AttachmentKind.Image, AttachmentKind.Document]));

        await DriveAPhotoAsync();

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem().Message!.Attachments.ShouldNotBeNull();
        _harness.Sent.ShouldBeEmpty();
    }

    // A message with no files is not a message about attachments, so no model has to accept it.
    [Fact]
    public async Task ATextTurnToAModelThatReadsNothing_IsUnaffected()
    {
        Register(new AgentCatalogEntry(
            "jack", "Jack", null, DefaultModel: "text-only/model", DefaultModelAttachmentKinds: []));

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = TelegramPollingHarness.TextMessage("/ask hello") });
        await _harness.RunAsync();

        (await _harness.ReceiveAsync()).ShouldHaveSingleItem();
        _harness.Sent.ShouldBeEmpty();
    }

    public void Dispose() => _harness.Dispose();

    // The catalogue the agent registers, and the household's jack: on the model the registered jack
    // runs unless the test says another.
    private void Register(params AgentCatalogEntry[] agents) => Register(agents, householdModel: null);

    private void Register(AgentCatalogEntry[] agents, string? householdModel)
    {
        new RegisterAgentsTool(_harness.Catalog).McpRun(agents);
        _harness.HouseholdRunsJackOn(
            householdModel ?? agents.First(a => a.Id == TelegramPollingHarness.AgentId).DefaultModel!);
    }

    private async Task DriveAPhotoAsync()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask what is this");
        message.Photo = TelegramPollingHarness.Photo();

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();
    }
}