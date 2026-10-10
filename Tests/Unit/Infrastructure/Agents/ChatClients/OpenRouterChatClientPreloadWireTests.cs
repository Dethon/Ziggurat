using System.Text.Json.Nodes;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;
using Tests.Unit.Domain.Skills;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// A preload as it reaches OpenRouter: a user-role note after the request and nothing else. The
// thread records the pair a load leaves, and that pair used to be sent as it stood — an assistant
// item no provider had issued, which each took in its own way. Anthropic's models were handed it
// with the host's reasoning dropped and read a turn of their own already begun (Haiku 5.5 never
// opened slow work with its spoken word, 0 of 10); DeepSeek's own host refused it without
// reasoning it could take back (HTTP 400, "The `reasoning_content` in the thinking mode must be
// passed back to the API" — again on 2026-10-10 for a pair sent with a summary or with none);
// Mistral cut its call ids to nine letters and digits and refused two that then agreed. A note
// is none of those things to anybody, so no call or reasoning of the host's making is ever on
// the wire — from this build's record or from any thread stored before it.
public class OpenRouterChatClientPreloadWireTests
{
    private static OpenRouterChatClient Client(CapturingSseHandler handler) =>
        new("http://openrouter.test/api/v1", "or-key", "deepseek/deepseek-v4.1-flash", transportHandler: handler);

    private static List<JsonObject> Input(CapturingSseHandler handler) =>
        JsonNode.Parse(handler.CapturedBody!)!["input"]!.AsArray().Select(i => i!.AsObject()).ToList();

    private static string Kind(JsonObject item) => item["role"]?.GetValue<string>() ?? item["type"]!.GetValue<string>();

    private static List<string> Texts(JsonObject message) =>
        message["content"]!.AsArray().Select(part => part!["text"]!.GetValue<string>()).ToList();

    private static SkillPreload HomePreload => new(SkillPreloadOutcome.Preloaded, [TestSkills.HomeWithIndex])
    {
        Reads = [new SkillPreloadRead("home-assistant", "/ha/setup-index.md", new JsonObject { ["content"] = "1: index" })]
    };

    [Fact]
    public async Task APreload_ReachesTheWireAsAUserNoteAfterTheRequest_AndNoCallTheHostMade()
    {
        var handler = new CapturingSseHandler();
        var sut = Client(handler);
        var messages = new List<ChatMessage> { new(ChatRole.User, "apaga el aire") };
        messages.AddRange(SkillLoadTool.AsPreloaded(HomePreload, "preload-abc"));

        await sut.GetStreamingResponseAsync(messages).ToListAsync();

        var input = Input(handler);
        input.Select(Kind).ShouldBe(["user", "user"], customMessage: handler.CapturedBody);
        Texts(input[0]).ShouldBe(["apaga el aire"]);
        var note = Texts(input[1]);
        note.Count.ShouldBe(2);
        note[0].ShouldStartWith("[Loaded for this request before your first call, by the host: the `home-assistant` skill. Do not load it again.]");
        note[0].ShouldContain("Read the index, then call the house.");
        note[1].ShouldStartWith("[Read for this request before your first call, by the host: `/ha/setup-index.md`. This is what `file_read` returned.]");
        note[1].ShouldContain("1: index");
        handler.CapturedBody!.ShouldNotContain("preload-abc");
    }

    // A thread stored before the note existed still holds the pair, the host's reasoning in front
    // of it. It is not migrated, and it is not sent either.
    [Fact]
    public async Task APairStoredWithTheHostsReasoning_ReachesTheWireAsTheSameNote()
    {
        var handler = new CapturingSseHandler();
        var sut = Client(handler);
        var reasoning = new TextReasoningContent("The request needs the home-assistant skill: loading it and reading /ha/setup-index.md as it says to.")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoningItemId"] = "host-reasoning-preload-1a2b3c4d" }
        };
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "apaga el aire"),
            new(ChatRole.Assistant,
            [
                reasoning,
                new FunctionCallContent("1-preload-1a2b3c4d", SkillLoadTool.Name,
                    new Dictionary<string, object?> { [SkillLoadTool.SkillNameParameter] = "home-assistant" }),
                new FunctionCallContent("2-preload-1a2b3c4d", SkillLoadTool.ReadToolName,
                    new Dictionary<string, object?> { [VfsFileReadTool.FilePathParameter] = "/ha/setup-index.md" })
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("1-preload-1a2b3c4d", SkillLoadTool.Wrapped(TestSkills.HomeWithIndex)),
                new FunctionResultContent("2-preload-1a2b3c4d", new JsonObject { ["content"] = "1: index" })
            ]),
            new(ChatRole.Assistant, "Hecho."),
            new(ChatRole.User, "y la luz")
        ];

        await sut.GetStreamingResponseAsync(messages).ToListAsync();

        var input = Input(handler);
        input.Select(Kind).ShouldBe(["user", "user", "assistant", "user"], customMessage: handler.CapturedBody);
        Texts(input[1])[0].ShouldContain("the `home-assistant` skill");
        handler.CapturedBody!.ShouldNotContain("host-reasoning-");
        handler.CapturedBody!.ShouldNotContain("preload-1a2b3c4d");
    }

    // The note is written onto the copy that is sent, after everything that asks who is speaking:
    // the last user-role message on the wire is the note, and the turn is still the person's.
    [Fact]
    public async Task ATurnWithAPreload_IsStillBilledToThePersonWhoAsked()
    {
        var inner = new Mock<IChatClient>();
        IReadOnlyList<ChatMessage>? sent = null;
        inner
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, _, _) => sent = [.. messages])
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })] }
            }.ToAsyncEnumerable());
        var metrics = new RecordingMetricsPublisher();
        using var sut = new OpenRouterChatClient(inner.Object, "test-model", metricsPublisher: metrics);
        var request = new ChatMessage(ChatRole.User, "apaga el aire");
        request.SetSenderId("fran");
        var messages = new List<ChatMessage> { request };
        messages.AddRange(SkillLoadTool.AsPreloaded(HomePreload, "preload-abc"));

        await sut.GetStreamingResponseAsync(messages).ToListAsync();

        sent.ShouldNotBeNull().Select(m => m.Role.Value).ShouldBe(["user", "user"]);
        sent[1].GetSenderId().ShouldBeNull();
        metrics.Published.OfType<TokenUsageEvent>().ShouldHaveSingleItem().Sender.ShouldBe("fran");
    }

    [Fact]
    public async Task AModelsOwnReasoning_KeepsTheAdaptersShape()
    {
        var handler = new CapturingSseHandler();
        var sut = Client(handler);
        var own = new TextReasoningContent("The AC is in the office.")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoningItemId"] = "rs_tmp_kgh82nx5128" }
        };
        var call = new FunctionCallContent("call_00_SCPBlKINIJOAjSnSzf8W3355", "domain__filesystem__exec", new Dictionary<string, object?> { ["path"] = "/ha" });
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "apaga el aire"),
            new(ChatRole.Assistant, [own, call]),
            new(ChatRole.Tool, [new FunctionResultContent(call.CallId, "ok")])
        ];

        await sut.GetStreamingResponseAsync(messages).ToListAsync();

        var reasoning = Input(handler).Single(i => Kind(i) == "reasoning");
        reasoning["id"]!.GetValue<string>().ShouldBe("rs_tmp_kgh82nx5128");
        reasoning["content"].ShouldBeNull();
        reasoning["summary"]!.AsArray().ShouldHaveSingleItem()!["text"]!.GetValue<string>().ShouldBe("The AC is in the office.");
    }
}