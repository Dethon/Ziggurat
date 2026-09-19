using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Prompts;
using Domain.Skills;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;
using Tests.Unit.Domain;
using Tests.Unit.Domain.Skills;

namespace Tests.Unit.Infrastructure.Agents.Skills;

// The provider's half of the head start: a message carrying a pending result is not judged
// again, and whatever that result says is what is inserted.
public class SkillsProviderPendingTests
{
    private static readonly PromptSkill _home = TestSkills.Home;

    [Fact]
    public async Task AMessageCarryingAPendingResult_CausesNoSecondJudgment_AndInsertsWhatItSays()
    {
        var preloader = new Mock<ISkillPreloader>(MockBehavior.Strict);
        var request = new ChatMessage(ChatRole.User, "enciende la luz");
        SkillPreloadPending.Attach(request, Task.FromResult(new SkillPreload(SkillPreloadOutcome.Preloaded, [_home])));

        var context = await Provide(preloader.Object, request);

        var inserted = Inserted(context, request);
        inserted.Select(m => m.Role.Value).ShouldBe(["assistant", "tool"]);
        inserted[0].Contents.OfType<FunctionCallContent>().ShouldHaveSingleItem().Name.ShouldBe(SkillLoadTool.Name);
        inserted[1].Contents.OfType<FunctionResultContent>().ShouldHaveSingleItem().Result!.ToString().ShouldNotBeNull().ShouldContain("Call the house.");
        preloader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task APendingResultWithARead_InsertsTheReadBesideTheLoad()
    {
        var preloader = new Mock<ISkillPreloader>(MockBehavior.Strict);
        var request = new ChatMessage(ChatRole.User, "enciende la luz");
        var index = new JsonObject { ["content"] = "1: ## Current Home Assistant setup" };
        SkillPreloadPending.Attach(request, Task.FromResult(new SkillPreload(SkillPreloadOutcome.Preloaded, [_home])
        {
            Reads = [new SkillPreloadRead(_home.Name, "/ha/setup-index.md", index)]
        }));

        var inserted = Inserted(await Provide(preloader.Object, request), request);

        inserted.Select(m => m.Role.Value).ShouldBe(["assistant", "tool"]);
        inserted[0].Contents.OfType<FunctionCallContent>().Select(c => c.Name)
            .ShouldBe([SkillLoadTool.Name, FileSystemToolFeature.Callable(VfsFileReadTool.Name)]);
        inserted[1].Contents.OfType<FunctionResultContent>().Last().Result.ShouldBeSameAs(index);
    }

    // A worker's request carries no patch of its own, only the parent turn's context — and a
    // worker spawned by a turn addressed to the local box must not ask a hosted judge either.
    [Fact]
    public async Task AWorkersRequest_NamesTheModelItsParentTurnAskedFor()
    {
        SkillPreloadRequest? asked = null;
        var preloader = new Mock<ISkillPreloader>();
        preloader.Setup(p => p.PreloadAsync(It.IsAny<SkillPreloadRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SkillPreloadRequest, CancellationToken>((r, _) => asked = r)
            .ReturnsAsync(SkillPreload.NotAsked);
        var request = new ChatMessage(ChatRole.User, "enciende la luz");
        request.SetConversationContext(new ConversationContext(
            "jack", "conv-1", "fran", new ReplyTarget("signalr", "conv-1"), "lemonade/qwen3"));

        await Provide(preloader.Object, request);

        asked.ShouldNotBeNull().ConfigPatchModel.ShouldBe("lemonade/qwen3");
    }

    [Theory]
    [InlineData(SkillPreloadOutcome.Deadline)]
    [InlineData(SkillPreloadOutcome.Error)]
    [InlineData(SkillPreloadOutcome.None)]
    public async Task APendingResultWithNoSkill_InsertsNothing(SkillPreloadOutcome outcome)
    {
        var preloader = new Mock<ISkillPreloader>(MockBehavior.Strict);
        var request = new ChatMessage(ChatRole.User, "enciende la luz");
        SkillPreloadPending.Attach(request, Task.FromResult(new SkillPreload(outcome, [])));

        var context = await Provide(preloader.Object, request);

        Inserted(context, request).ShouldBeEmpty();
        preloader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task APendingResult_IsTakenOnce()
    {
        var preloader = new Mock<ISkillPreloader>();
        preloader.Setup(p => p.PreloadAsync(It.IsAny<SkillPreloadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SkillPreload.NotAsked);
        var request = new ChatMessage(ChatRole.User, "enciende la luz");
        SkillPreloadPending.Attach(request, Task.FromResult(new SkillPreload(SkillPreloadOutcome.Preloaded, [_home])));

        Inserted(await Provide(preloader.Object, request), request).ShouldNotBeEmpty();
        Inserted(await Provide(preloader.Object, request), request).ShouldBeEmpty();

        preloader.Verify(p => p.PreloadAsync(It.IsAny<SkillPreloadRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The framework merges the request into what a provider returns; what the provider added is
    // everything else.
    private static List<ChatMessage> Inserted(AIContext context, ChatMessage request) =>
        (context.Messages ?? []).Where(m => !ReferenceEquals(m, request)).ToList();

#pragma warning disable MAAI001 // Driving a provider by hand is the only way to test it alone.
    private static async Task<AIContext> Provide(ISkillPreloader preloader, ChatMessage request)
    {
        using var provider = new SkillsProvider(_ => [_home], preloader);
        return await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(new FakeAiAgent(), null, new AIContext { Messages = [request] }),
            CancellationToken.None);
    }
#pragma warning restore MAAI001
}