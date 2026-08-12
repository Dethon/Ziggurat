using Domain.DTOs.Channel;
using McpChannelTelegram.McpTools;
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
    public async Task WithTheAgentAbsentFromTheCatalogue_AttachmentsGoThrough()
    {
        Register(new AgentCatalogEntry(
            "someone-else", "Other", null,
            DefaultModel: "text-only/model",
            DefaultModelAttachmentKinds: []));

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

    private void Register(params AgentCatalogEntry[] agents) =>
        new RegisterAgentsTool(_harness.Catalog).McpRun(agents);

    private async Task DriveAPhotoAsync()
    {
        var message = TelegramPollingHarness.MediaMessage(caption: "/ask what is this");
        message.Photo = TelegramPollingHarness.Photo();

        await _harness.ReceiveAsync();
        _harness.Enqueue(new Update { Id = 1, Message = message });
        await _harness.RunAsync();
    }
}