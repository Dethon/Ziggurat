using System.Text;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;
using Tests.Unit.Infrastructure.Agents.ChatClients;

namespace Tests.Unit.Infrastructure.Agents;

// The chat client the factory builds asks the deployment's own capability answers whether a model
// watches video — the provider's catalogue for a hosted model, the box's own list for a Lemonade
// one — so a clip reaches a model that only sees pictures as its stills.
public sealed class MultiAgentFactoryClipTests
{
    private const string SeesPictures = "openai/sees-pictures";

    private static readonly AttachmentReference _still = new()
    {
        Id = "sugary/still-1", FileName = "still-1.jpg", MediaType = "image/jpeg", SizeBytes = 4
    };

    private static readonly AttachmentReference _clip = new()
    {
        Id = "sugary/clip", FileName = "plate.mp4", MediaType = "video/mp4", SizeBytes = 4, Stills = [_still.Id]
    };

    [Fact]
    public async Task AHostedModelTheCatalogueListsWithoutVideo_IsSentTheStills()
    {
        var wire = new RecordingResponsesHandler();
        var catalog = new Mock<IModelCapabilityCatalog>();
        catalog.Setup(c => c.GetAcceptedAttachmentKinds(SeesPictures)).Returns([AttachmentKind.Image]);
        var services = Services();
        services.Setup(sp => sp.GetService(typeof(IModelCapabilityCatalog))).Returns(catalog.Object);

        using var client = Factory(services).CreateChatClient(SeesPictures, transportHandler: wire);
        await client.GetResponseAsync([ClipMessage()]);

        Types(wire.Bodies.ShouldHaveSingleItem()).ShouldBe(["input_text", "input_image"]);
    }

    [Fact]
    public async Task ALemonadeModel_IsSentTheStills()
    {
        var wire = new RecordingResponsesHandler();
        var services = Services();
        services.Setup(sp => sp.GetService(typeof(ILemonadeModelSource)))
            .Returns(new FixedLemonadeModels([new LemonadeModel("sees", [AttachmentKind.Image], null)]));
        services.Setup(sp => sp.GetService(typeof(LemonadeChatHostOptions)))
            .Returns(new LemonadeChatHostOptions { ApiUrl = "http://lemonade.invalid:13305/api/v1" });

        using var client = Factory(services).CreateChatClient(
            "configured/model", lemonadeTransportHandler: wire);
        await client.GetResponseAsync(
            [ClipMessage()], new ChatOptions { ModelId = LemonadeModelId.Namespaced("sees") });

        Types(wire.Bodies.ShouldHaveSingleItem()).ShouldBe(["input_text", "input_image"]);
    }

    private static ChatMessage ClipMessage()
    {
        var message = new ChatMessage(ChatRole.User, "my lunch");
        message.SetAttachments([_clip, _still]);
        message.SetAttachmentChannelId("sugary");
        return message;
    }

    private static IReadOnlyList<string> Types(JsonObject body) =>
        body["input"]!.AsArray()
            .OfType<JsonObject>()
            .Where(item => item["role"]?.GetValue<string>() == "user")
            .SelectMany(item => item["content"] as JsonArray ?? [])
            .Select(part => part!["type"]!.GetValue<string>())
            .ToList();

    private static Mock<IServiceProvider> Services()
    {
        var services = new Mock<IServiceProvider>();
        services.Setup(sp => sp.GetService(typeof(IAttachmentSource))).Returns(new IdBytes());
        return services;
    }

    private static MultiAgentFactory Factory(Mock<IServiceProvider> services) =>
        new(
            services.Object,
            Mock.Of<IAgentDefinitionProvider>(),
            new OpenRouterConfig { ApiUrl = "http://openrouter.invalid/api/v1", ApiKey = "key" },
            Mock.Of<IDomainToolRegistry>());

    private sealed class FixedLemonadeModels(IReadOnlyList<LemonadeModel> models) : ILemonadeModelSource
    {
        public IReadOnlyList<LemonadeModel> Current => models;
    }

    private sealed class IdBytes : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct) =>
            Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(attachmentId));
    }
}