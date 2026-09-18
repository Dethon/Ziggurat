using System.Text.Json.Nodes;
using Domain.Skills;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Unit.Domain.Skills;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// The preload pair as it reaches OpenRouter. A thinking model's own load arrives as a reasoning
// item followed by its calls, and DeepSeek's first-party host refuses an assistant call message
// without one ("The `reasoning_content` in the thinking mode must be passed back to the API",
// HTTP 400) — OpenRouter then falls back to another provider, which costs the failed attempt and
// the prompt cache the conversation had warmed. A model's own reasoning is recovered by OpenRouter
// under the call ids it issued, so the adapter's summary-only item is enough for it; the pair's
// calls are ids OpenRouter never issued, so its reasoning goes out in full, in the one shape every
// deployed provider took on 2026-09-18: a `reasoning` item whose content is non-empty
// `reasoning_text`. A summary alone, or empty text, is the same 400.
public class OpenRouterChatClientPreloadWireTests
{
    private static OpenRouterChatClient Client(CapturingSseHandler handler) =>
        new("http://openrouter.test/api/v1", "or-key", "deepseek/deepseek-v4.1-flash", transportHandler: handler);

    private static List<JsonObject> Input(CapturingSseHandler handler) =>
        JsonNode.Parse(handler.CapturedBody!)!["input"]!.AsArray().Select(i => i!.AsObject()).ToList();

    private static string Kind(JsonObject item) => item["role"]?.GetValue<string>() ?? item["type"]!.GetValue<string>();

    [Fact]
    public async Task ThePreloadPair_ReachesTheWireAsAReasoningItemBeforeItsCalls()
    {
        var handler = new CapturingSseHandler();
        var sut = Client(handler);
        var preload = new SkillPreload(SkillPreloadOutcome.Preloaded, [TestSkills.HomeWithIndex])
        {
            Reads = [new SkillPreloadRead("home-assistant", "/ha/setup-index.md", new JsonObject { ["content"] = "1: index" })]
        };
        var messages = new List<ChatMessage> { new(ChatRole.User, "apaga el aire") };
        messages.AddRange(SkillLoadTool.AsPreloaded(preload, "preload-abc"));

        await sut.GetStreamingResponseAsync(messages).ToListAsync();

        var input = Input(handler);
        input.Select(Kind).ShouldBe(
            ["user", "reasoning", "function_call", "function_call", "function_call_output", "function_call_output"],
            customMessage: handler.CapturedBody);

        var reasoning = input[1];
        var text = reasoning["content"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        text["type"]!.GetValue<string>().ShouldBe("reasoning_text");
        text["text"]!.GetValue<string>().ShouldContain("home-assistant");
        reasoning["summary"]!.AsArray().ShouldBeEmpty();
        input[2]["call_id"]!.GetValue<string>().ShouldBe("preload-abc-1");
        input[3]["call_id"]!.GetValue<string>().ShouldBe("preload-abc-read-1");
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