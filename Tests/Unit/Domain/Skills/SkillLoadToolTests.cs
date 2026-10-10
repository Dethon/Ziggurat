using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Skills;

// What a preload leaves in the thread: the messages the model's own load and its first read would
// have left, so "already loaded" is one question asked of the history whoever loaded. The calls
// are marked as the host's by their ids, because that record is never what a model is sent
// (SkillPreloadNoteTests) — and so it carries no reasoning of the host's making either.
public class SkillLoadToolTests
{
    [Fact]
    public void AsPreloaded_ASkillWithARead_LeavesTheLoadPairThenTheReadPair()
    {
        var index = new JsonObject { ["filePath"] = "/ha/setup-index.md", ["content"] = "1: ## Current Home Assistant setup" };
        var preload = new SkillPreload(SkillPreloadOutcome.Preloaded, [TestSkills.HomeWithIndex])
        {
            Reads = [new SkillPreloadRead("home-assistant", "/ha/setup-index.md", index)]
        };

        var messages = SkillLoadTool.AsPreloaded(preload, "preload-abc");

        messages.Select(m => m.Role.Value).ShouldBe(["assistant", "tool"]);
        var calls = messages[0].Contents.Select(c => c.ShouldBeOfType<FunctionCallContent>()).ToList();
        calls.Select(c => c.Name).ShouldBe([SkillLoadTool.Name, FileSystemToolFeature.Callable(VfsFileReadTool.Name)]);
        calls.Select(c => c.CallId).ShouldBe(["preload-abc-1", "preload-abc-2"]);
        calls.ShouldAllBe(c => SkillLoadTool.IsHostCall(c.CallId));
        calls[1].Arguments!.ShouldContainKeyAndValue(VfsFileReadTool.FilePathParameter, "/ha/setup-index.md");

        var results = messages[1].Contents.OfType<FunctionResultContent>().ToList();
        results.Select(r => r.CallId).ShouldBe(["preload-abc-1", "preload-abc-2"]);
        results[0].Result!.ToString().ShouldNotBeNull().ShouldContain("Read the index, then call the house.");
        results[1].Result.ShouldBeSameAs(index);
    }

    [Fact]
    public void AsPreloaded_ASkillWithoutARead_LeavesTheLoadPairAlone()
    {
        var preload = new SkillPreload(SkillPreloadOutcome.Preloaded, [TestSkills.Timers]);

        var messages = SkillLoadTool.AsPreloaded(preload, "preload-abc");

        messages.Select(m => m.Role.Value).ShouldBe(["assistant", "tool"]);
        messages[0].Contents.OfType<FunctionCallContent>().ShouldHaveSingleItem().Name.ShouldBe(SkillLoadTool.Name);
        messages[1].Contents.OfType<FunctionResultContent>().ShouldHaveSingleItem();
        messages[0].Contents.OfType<TextReasoningContent>().ShouldBeEmpty();
    }

    // Every scheme a preload's calls were ever stored under is still in somebody's thread, and
    // each has to be told from a call a model made: the turn's prefix then an ordinal, the reads
    // counted apart, or the ordinal first.
    [Theory]
    [InlineData("preload-1a2b3c4d-1")]
    [InlineData("preload-1a2b3c4d-read-1")]
    [InlineData("1-preload-1a2b3c4d")]
    [InlineData("12-preload-1a2b3c4d")]
    public void IsHostCall_KnowsEveryIdAPreloadWasStoredUnder(string callId) =>
        SkillLoadTool.IsHostCall(callId).ShouldBeTrue();

    [Theory]
    [InlineData("call_00_SCPBlKINIJOAjSnSzf8W3355")]
    [InlineData("toolu_01FcAJJFvL7xTsCojgL1CckL")]
    [InlineData("fc_tmp_h1g15pmtj9")]
    [InlineData("a1b2c3d4e")]
    [InlineData("my-preload-1")]
    [InlineData("")]
    [InlineData(null)]
    public void IsHostCall_TakesNoModelsOwnCallForTheHosts(string? callId) =>
        SkillLoadTool.IsHostCall(callId).ShouldBeFalse();

    [Fact]
    public void LoadedIn_SeesAPreloadedSkill_AndNotItsRead()
    {
        var preload = new SkillPreload(SkillPreloadOutcome.Preloaded, [TestSkills.HomeWithIndex])
        {
            Reads = [new SkillPreloadRead("home-assistant", "/ha/setup-index.md", new JsonObject())]
        };

        SkillLoadTool.LoadedIn(SkillLoadTool.AsPreloaded(preload, "preload-abc")).ShouldBe(["home-assistant"]);
    }

    // Not migrated and still loaded: a pair an earlier build stored, with the reasoning it wrote
    // in front of its calls, read back from the store.
    [Fact]
    public void LoadedIn_SeesAPairAnEarlierBuildStored()
    {
        var reasoning = new TextReasoningContent("The request needs the home-assistant skill: loading it.")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoningItemId"] = "host-reasoning-preload-1a2b3c4d" }
        };
        List<ChatMessage> stored =
        [
            new(ChatRole.User, "enciende la luz"),
            new(ChatRole.Assistant,
            [
                reasoning,
                new FunctionCallContent("1-preload-1a2b3c4d", SkillLoadTool.Name,
                    new Dictionary<string, object?> { [SkillLoadTool.SkillNameParameter] = "home-assistant" })
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("1-preload-1a2b3c4d", "body")])
        ];
        var readBack = stored.Select(m => JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(m))!);

        SkillLoadTool.LoadedIn(readBack).ShouldBe(["home-assistant"]);
    }
}