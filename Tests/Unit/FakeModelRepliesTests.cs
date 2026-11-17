using System.Text.Json.Nodes;
using Domain.Conversations;
using Shouldly;
using Tests.Integration.Fixtures;
using static Tests.Integration.Fixtures.FakeModelReplies;

namespace Tests.Unit;

public class FakeModelRepliesTests
{
    private const string GlobDemand =
        "IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with "
        + "pattern **/*. After the tool is called say 'Done' without caring for its result.";

    private static JsonObject User(string text) => new()
    {
        ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text })
    };

    private static JsonObject Assistant(string text) => new()
    {
        ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text })
    };

    private static JsonObject Call() => new() { ["type"] = "function_call", ["call_id"] = "c", ["name"] = "domain__glob", ["arguments"] = "{}" };

    private static JsonObject Output() => new() { ["type"] = "function_call_output", ["call_id"] = "c", ["output"] = "[]" };

    private static JsonObject Request(params JsonObject[] items) => new()
    {
        ["input"] = new JsonArray([.. items]),
        ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "domain__glob" })
    };

    private static string Said(Reply reply) => reply.Output.ShouldBeOfType<Text>().Whole;

    [Fact]
    public void AnyOtherMessage_GetsTheShortReply() =>
        Said(For(Request(User("Hello, answer in one short sentence.")))).ShouldBe(ShortReply);

    [Theory]
    [InlineData("Reply with only the word ALPHA1a2b. Do not call any tools.", "ALPHA1a2b")]
    [InlineData("Plan 1a2b: answer only OK.", "OK")]
    [InlineData("Edited 1a2b: answer only YES. Do not call any tools.", "YES")]
    public void AWordAskedFor_IsTheReply(string message, string word) =>
        Said(For(Request(User(message)))).ShouldBe(word);

    [Fact]
    public void StepsSentTogether_AreAnsweredTogether() =>
        Said(For(Request(
                User("Reply with the word ONE1. Do not call any tools."),
                User("And in the same reply, after it, the word TWO1. Do not call any tools."))))
            .ShouldBe("ONE1 TWO1");

    [Fact]
    public void AnEarlierTurnsWord_IsNotAskedAgain() =>
        Said(For(Request(User("Reply with only the word ALPHA1."), Assistant("ALPHA1"), User("Hello"))))
            .ShouldBe(ShortReply);

    [Fact]
    public void AGlobDemand_CallsTheGlobTool() =>
        For(Request(User(GlobDemand))).Output.ShouldBeOfType<ToolCall>().Name.ShouldBe("domain__glob");

    [Fact]
    public void OnceTheToolAnswered_SaysWhatTheDemandAskedFor() =>
        Said(For(Request(User(GlobDemand), Call(), Output()))).ShouldBe("Done");

    [Fact]
    public void OnceTheToolAnswered_TheWordTheDemandNamesIsSaid() =>
        Said(For(Request(User("You MUST call a tool now. Use your glob tool. Then reply with the word AFTER1."), Call(), Output())))
            .ShouldBe("AFTER1");

    [Fact]
    public void ASteerAfterTheCall_IsWhatIsAnswered() =>
        Said(For(Request(
                User("You MUST call a tool now. Use your glob tool. Then reply with the word AFTER1."),
                Call(), Output(),
                User("Reply with only the word STEER1. Do not call any tools."))))
            .ShouldBe("STEER1");

    [Fact]
    public void ATurnedDownDemandsWord_IsNotAnsweredInTheNextTurn() =>
        Said(For(Request(
                User("You MUST call a tool now. Use your glob tool. Then reply with the word AFTER1."),
                User("Reply with only the word BRAVO1."))))
            .ShouldBe("BRAVO1");

    [Fact]
    public void ALongStory_StreamsSlowlyEnoughToBeStopped()
    {
        var story = For(Request(User("Write a very long and detailed story about a space adventure."))).Output.ShouldBeOfType<Text>();

        (story.ChunkDelay * story.Chunks.Count).ShouldBeGreaterThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void ReasoningAskedForWithASummary_IsStreamed()
    {
        var request = Request(User("Compare 9.11 and 9.9."));
        request["reasoning"] = new JsonObject { ["effort"] = "low", ["summary"] = "auto" };

        For(request).Reasoning.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("none", "auto")]
    [InlineData("low", null)]
    public void ReasoningOffOrUnsummarised_IsNotStreamed(string effort, string? summary)
    {
        var request = Request(User("Compare 9.11 and 9.9."));
        request["reasoning"] = new JsonObject { ["effort"] = effort, ["summary"] = summary };

        For(request).Reasoning.ShouldBeNull();
    }

    [Fact]
    public void AStrictSchema_IsAnsweredWithAnInstanceOfIt()
    {
        var request = Request(User("Estimate this meal."));
        request["text"] = JsonNode.Parse("""
            {"format":{"type":"json_schema","name":"meal","strict":true,"schema":{
              "type":"object","additionalProperties":false,"required":["items","note"],
              "properties":{
                "items":{"type":"array","items":{"$ref":"#/$defs/item"}},
                "note":{"type":["string","null"]}},
              "$defs":{"item":{"type":"object","properties":{
                "grams":{"type":"number"},"confidence":{"type":"string","enum":["low","high"]}}}}}}}
            """)!.AsObject();

        var answer = JsonNode.Parse(Said(For(request)))!;

        answer["items"]![0]!["grams"]!.GetValue<double>().ShouldBeGreaterThan(0);
        answer["items"]![0]!["confidence"]!.GetValue<string>().ShouldBe("low");
        answer["note"]!.GetValue<string>().ShouldNotBeEmpty();
    }

    [Fact]
    public void AContinue_CarriesOnTheStoryItContinues()
    {
        var story = For(Request(
            User("Write a very long and detailed story about a lighthouse keeper."),
            Assistant("Chapter line 1 of the voyage went on."),
            User(Continuation.Instruction))).Output.ShouldBeOfType<Text>();

        story.Chunks.Count.ShouldBe(StoryChunks);
    }

    // The chat keys a streamed message by its id, so no two replies may share one.
    [Fact]
    public void EveryReply_StreamsUnderIdsOfItsOwn()
    {
        var request = Request(User("Hello"));

        var first = FakeResponsesWire.Stream(For(request));
        var second = FakeResponsesWire.Stream(For(request));

        MessageIds(first).ShouldHaveSingleItem();
        MessageIds(first).ShouldNotBe(MessageIds(second));
    }

    private static IReadOnlyList<string> MessageIds(string sse) =>
    [
        .. System.Text.RegularExpressions.Regex.Matches(sse, "\"(?:id|item_id)\":\"(msg_[^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
    ];
}