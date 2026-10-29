using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Prompts;
using Domain.Skills;
using Domain.Tools.Sugary.Estimation;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using McpServerSugary.Channel;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit.Domain.Prompts;
using Tests.Unit.Domain.Skills;
using Tests.Unit.Infrastructure.Agents.ChatClients;

namespace Tests.Integration.Agents;

// The shipped agent definitions dialling the real Sugary server, read where it counts: the request
// that leaves for OpenRouter. The sugary agent is an estimator and nothing else — the first paid
// pass saw it read the log and load the skill on estimate turns, at three times the cost and with
// six meals of 78 left unanswered — so its request carries the estimating words the server serves
// and no tool at all, and no judge is asked which skill it needs. Jonas dials the same server for
// her log: the mount and the skill reach him, the estimating words do not.
public sealed class SugaryAgentRequestTests
{
    private static readonly SkillPreloadSettings _preload = new() { DeadlineMs = 600 };

    [Fact]
    public async Task AnEstimateTurnOfTheSugaryAgent_CarriesTheEstimatingPromptAndNoTools()
    {
        await using var server = await SugaryServer.StartAsync();
        var wire = new RecordingResponsesHandler();
        var judge = new ScriptedJudge(_ => JudgeAnswers.Sure(SugarySkill.Name));
        await using var agent = Agent("sugary", server, wire, judge);
        var session = await agent.CreateSessionAsync();

        await agent.RunStreamingAsync([Estimate()], session).ToListAsync();

        var body = wire.Bodies.ShouldHaveSingleItem();
        body.ContainsKey("tools").ShouldBeFalse($"tools went out: {body["tools"]?.ToJsonString()}");
        body["instructions"]!.GetValue<string>().ShouldContain("## Estimating carbs");
        agent.GetSkills(session).ShouldBeEmpty();
        judge.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task JonasDiallingTheSugaryServer_GetsTheMountAndTheSkillButNotTheEstimatingPrompt()
    {
        await using var server = await SugaryServer.StartAsync();
        var wire = new RecordingResponsesHandler();
        await using var agent = Agent("jonas", server, wire, judge: null);
        var session = await agent.CreateSessionAsync();

        await agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, "¿Cuándo comió paella por última vez?")], session).ToListAsync();

        var body = wire.Bodies.ShouldHaveSingleItem();
        var instructions = body["instructions"]!.GetValue<string>();
        instructions.ShouldNotContain("## Estimating carbs");
        instructions.ShouldContain("`/sugary`");
        Tools(body).ShouldContain("domain__filesystem__text_search");
        Tools(body).ShouldContain(SkillLoadTool.Name);
        agent.GetSkills(session).Select(s => s.Name).ShouldBe([SugarySkill.Name]);
    }

    // The shipped definition, its endpoints pointed at this server, projected as the factory
    // projects it — so a switch that lives in the definition is what is being exercised.
    private static McpAgent Agent(string id, SugaryServer server, RecordingResponsesHandler wire, ScriptedJudge? judge)
    {
        var shipped = AgentPromptFixture.Agents.Single(a => a.Id == id);
        var definition = shipped with { McpServerEndpoints = [server.Endpoint] };
        var spec = AgentSpecProjection.ForAgent(
            definition, new AgentKey($"conv-{Guid.NewGuid():N}", id), "fran",
            new OpenRouterConfig { ApiUrl = "http://localhost/api/v1", ApiKey = "test-key" },
            FixedPatchableModelSource.None, lemonadeHostAddress: null, McpTestSecret.Credentials,
            new DeploymentEndpoints(definition.McpServerEndpoints), logger: null);
        var client = new OpenRouterChatClient(
            "http://localhost/api/v1", "test-key", spec.Model, transportHandler: wire);

        return new McpAgent(
            spec, client, new InMemoryThreadStore().Object, NoOpMetricsPublisher.Instance, TimeProvider.System,
            [], [],
            skillPreloader: judge is null ? null : new SkillPreloader(judge, _preload, TimeProvider.System));
    }

    private static ChatMessage Estimate()
    {
        var message = new ChatMessage(
            ChatRole.User,
            MealTurn.Text(new MealInput(0, "Arroz blanco con pollo al curry y un trozo de pan.", null, "es")));
        message.SetSenderId(SugaryChannel.Sender);
        message.SetResponseFormat(MealEstimateSchema.Format);
        return message;
    }

    private static IReadOnlyList<string> Tools(JsonObject body) =>
        [.. body["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
}