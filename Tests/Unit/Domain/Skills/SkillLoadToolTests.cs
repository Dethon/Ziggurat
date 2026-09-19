using System.Text.Json.Nodes;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Skills;

// What a preload leaves in the conversation: exactly the messages the model's own load and its
// first read would have left, so the next turn — and the model — cannot tell who made them. That
// includes the reasoning a thinking model leaves before its calls, marked as the host's for the
// wire, because a host in thinking mode can refuse calls that arrive without any.
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
        var reasoning = messages[0].Contents[0].ShouldBeOfType<TextReasoningContent>();
        reasoning.Text.ShouldContain("home-assistant");
        reasoning.Text.ShouldContain("/ha/setup-index.md");
        SkillLoadTool.IsHostReasoning(reasoning.AdditionalProperties![SkillLoadTool.ReasoningItemIdKey]!.ToString()).ShouldBeTrue();
        var calls = messages[0].Contents.OfType<FunctionCallContent>().ToList();
        calls.Select(c => c.Name).ShouldBe([SkillLoadTool.Name, FileSystemToolFeature.Callable(VfsFileReadTool.Name)]);
        calls[0].CallId.ShouldBe("preload-abc-1");
        calls[1].CallId.ShouldBe("preload-abc-read-1");
        calls[1].Arguments!.ShouldContainKeyAndValue(VfsFileReadTool.FilePathParameter, "/ha/setup-index.md");

        var results = messages[1].Contents.OfType<FunctionResultContent>().ToList();
        results.Select(r => r.CallId).ShouldBe(["preload-abc-1", "preload-abc-read-1"]);
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
        var reasoning = messages[0].Contents.OfType<TextReasoningContent>().ShouldHaveSingleItem();
        reasoning.Text.ShouldContain("countdown-timers");
        reasoning.Text.ShouldNotContain("reading");
    }

    [Fact]
    public void IsHostReasoning_TellsTheHostsPartFromAModelsOwn()
    {
        SkillLoadTool.IsHostReasoning(SkillLoadTool.HostReasoningIdPrefix + "preload-abc").ShouldBeTrue();
        SkillLoadTool.IsHostReasoning("rs_tmp_kgh82nx5128").ShouldBeFalse();
        SkillLoadTool.IsHostReasoning(null).ShouldBeFalse();
    }

    [Fact]
    public void LoadedIn_SeesAPreloadedSkill_AndNotItsRead()
    {
        var preload = new SkillPreload(SkillPreloadOutcome.Preloaded, [TestSkills.HomeWithIndex])
        {
            Reads = [new SkillPreloadRead("home-assistant", "/ha/setup-index.md", new JsonObject())]
        };

        SkillLoadTool.LoadedIn(SkillLoadTool.AsPreloaded(preload, "preload-abc")).ShouldBe(["home-assistant"]);
    }
}