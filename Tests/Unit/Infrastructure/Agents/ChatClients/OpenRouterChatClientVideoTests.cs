using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Exceptions;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// A clip arrives with still frames taken from it, stored as pictures of the same message and named
// by the clip's reference. Which of them the model is sent is decided at the send, for the model
// the turn resolved to: the clip to a model that watches video, the stills and the photos to one
// that only sees pictures, never both. Asserted on the request that leaves for OpenRouter, because
// the clip only becomes a video part on the way out.
public sealed class OpenRouterChatClientVideoTests
{
    private const string Watches = "google/watches";
    private const string SeesPictures = "openai/sees-pictures";
    private const string TextOnly = "z-ai/text-only";

    private static readonly IReadOnlyDictionary<string, AttachmentKind[]> _kinds =
        new Dictionary<string, AttachmentKind[]>
        {
            [Watches] = [AttachmentKind.Image, AttachmentKind.Video],
            [SeesPictures] = [AttachmentKind.Image],
            [TextOnly] = []
        };

    private static readonly AttachmentReference _still1 = Reference("sugary/still-1", "still-1.jpg", "image/jpeg");
    private static readonly AttachmentReference _still2 = Reference("sugary/still-2", "still-2.jpg", "image/jpeg");
    private static readonly AttachmentReference _photo = Reference("sugary/photo", "plate.jpg", "image/jpeg");

    private static readonly AttachmentReference _clip =
        Reference("sugary/clip", "plate.mp4", "video/mp4") with { Stills = [_still1.Id, _still2.Id] };

    [Fact]
    public async Task AClipWithStills_OnAModelThatTakesVideo_SendsTheClipAndNotItsStills()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire, Watches);

        await agent.RunStreamingAsync([MealMessage()]).ToListAsync();

        var parts = UserParts(wire.Bodies.ShouldHaveSingleItem());
        parts.Where(IsVideo).ShouldHaveSingleItem()["video_url"]!.GetValue<string>()
            .ShouldBe(DataUrl(_clip));
        ImageUrls(parts).ShouldBe([DataUrl(_photo)]);
    }

    [Fact]
    public async Task AClipWithStills_OnAModelThatOnlySeesPictures_SendsTheStillsAndThePhotosButNeverTheClip()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire, SeesPictures);

        await agent.RunStreamingAsync([MealMessage()]).ToListAsync();

        var parts = UserParts(wire.Bodies.ShouldHaveSingleItem());
        parts.Where(IsVideo).ShouldBeEmpty();
        ImageUrls(parts).ShouldBe([DataUrl(_still1), DataUrl(_still2), DataUrl(_photo)], ignoreOrder: true);
    }

    // A patch moves one turn onto another model, and the wire takes or rejects what that model
    // takes — so the choice is the patched model's, both ways round.
    [Theory]
    [InlineData(Watches, SeesPictures, false)]
    [InlineData(SeesPictures, Watches, true)]
    public async Task APatchedTurn_IsDecidedByThePatchedModel(string agentModel, string patched, bool sendsTheClip)
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire, agentModel);
        var message = MealMessage();
        message.SetConfigPatch(new AgentConfigPatch { Model = patched });

        await agent.RunStreamingAsync([message]).ToListAsync();

        var body = wire.Bodies.ShouldHaveSingleItem();
        body["model"]!.GetValue<string>().ShouldBe(patched);
        UserParts(body).Any(IsVideo).ShouldBe(sendsTheClip);
        ImageUrls(UserParts(body)).Count.ShouldBe(sendsTheClip ? 1 : 3);
    }

    // An answer that silently ignores the meal it was shown is worse than none: nothing leaves for
    // the provider, and what the channel is told names the model and why.
    [Fact]
    public async Task AModelThatTakesNeitherVideoNorImages_HasTheTurnRefused()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire, TextOnly);

        var refusal = await Should.ThrowAsync<AttachmentRefusedException>(
            () => agent.RunStreamingAsync([MealMessage()]).ToListAsync().AsTask());

        wire.Bodies.ShouldBeEmpty();
        refusal.Message.ShouldContain(TextOnly);
        refusal.Message.ShouldContain("plate.mp4");
    }

    [Fact]
    public async Task APatchOntoAModelThatTakesNeither_HasTheTurnRefused()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire, Watches);
        var message = MealMessage();
        message.SetConfigPatch(new AgentConfigPatch { Model = TextOnly });

        await Should.ThrowAsync<AttachmentRefusedException>(
            () => agent.RunStreamingAsync([message]).ToListAsync().AsTask());

        wire.Bodies.ShouldBeEmpty();
    }

    // With no stills there is nothing a model that only sees pictures could be shown in the clip's
    // place, and leaving it out would be the silent answer the refusal exists to prevent.
    [Fact]
    public async Task AClipWithNoStills_OnAModelThatOnlySeesPictures_HasTheTurnRefused()
    {
        var wire = new RecordingResponsesHandler();
        await using var agent = CreateAgent(wire, SeesPictures);

        await Should.ThrowAsync<AttachmentRefusedException>(
            () => agent.RunStreamingAsync([MealMessage(_clip with { Stills = null }, _photo)]).ToListAsync().AsTask());

        wire.Bodies.ShouldBeEmpty();
    }

    private static ChatMessage MealMessage(params AttachmentReference[] attachments)
    {
        var message = new ChatMessage(ChatRole.User, "my lunch");
        message.SetAttachments(attachments.Length > 0 ? attachments : [_clip, _still1, _still2, _photo]);
        message.SetAttachmentChannelId("sugary");
        return message;
    }

    private static AttachmentReference Reference(string id, string fileName, string mediaType) =>
        new() { Id = id, FileName = fileName, MediaType = mediaType, SizeBytes = 4 };

    // Each file's bytes are its own id, so a part on the wire says which file it carries.
    private static string DataUrl(AttachmentReference attachment) =>
        $"data:{attachment.MediaType};base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(attachment.Id))}";

    private static bool IsVideo(JsonObject part) => part["type"]?.GetValue<string>() == "input_video";

    private static IReadOnlyList<string> ImageUrls(IEnumerable<JsonObject> parts) =>
        parts
            .Where(p => p["type"]?.GetValue<string>() == "input_image")
            .Select(p => p["image_url"]!.GetValue<string>())
            .ToList();

    // Every content part of every user message in the request.
    private static IReadOnlyList<JsonObject> UserParts(JsonObject body) =>
        body["input"]!.AsArray()
            .OfType<JsonObject>()
            .Where(item => item["role"]?.GetValue<string>() == "user")
            .SelectMany(item => item["content"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .ToList();

    private static McpAgent CreateAgent(
        RecordingResponsesHandler wire,
        string model,
        IThreadStateStore? store = null,
        int hydrationDepthMessages = 20)
    {
        var client = new OpenRouterChatClient(
            "http://localhost/api/v1",
            "test-key",
            model,
            transportHandler: wire,
            attachmentSource: new IdBytesAttachmentSource(),
            hydrationDepthMessages: hydrationDepthMessages,
            modelAcceptsImages: m => _kinds[m].Contains(AttachmentKind.Image),
            modelAcceptsVideo: m => _kinds[m].Contains(AttachmentKind.Video));
        return new McpAgent(
            TestAgentSpec.Default with
            {
                Model = model,
                PatchableModels = new FixedPatchableModelSource([Watches, SeesPictures, TextOnly])
            },
            client,
            store ?? new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);
    }

    private sealed class IdBytesAttachmentSource : IAttachmentSource
    {
        public Task<byte[]?> FetchAsync(string channelId, string attachmentId, CancellationToken ct) =>
            Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(attachmentId));
    }
}