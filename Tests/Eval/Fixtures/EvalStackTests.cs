using Agent.Settings;
using Domain.DTOs;
using Shouldly;

namespace Tests.Eval.Fixtures;

// The one thing a stack changes about the shipped definition: where its servers are. No model
// and no container — what is pinned is that a worker is pointed at the stack as its parent is,
// because a worker that really runs dials what its own profile names.
public class EvalStackTests
{
    private static readonly AgentSettings _shipped = new()
    {
        OpenRouter = new OpenRouterConfiguration { ApiUrl = "https://openrouter.ai/api/v1/", ApiKey = "" },
        Redis = new RedisConfiguration { ConnectionString = "localhost" },
        Agents =
        [
            new AgentDefinition
            {
                Id = "jonas", Name = "Jonas", Model = "m",
                McpServerEndpoints = ["http://mcp-vault:8080/mcp", "http://mcp-idealista:8080/mcp"]
            }
        ],
        SubAgents =
        [
            new SubAgentDefinition
            {
                Id = "jonas-worker", Name = "Jonas Worker",
                McpServerEndpoints = ["http://mcp-websearch:8080/mcp", "http://mcp-idealista:8080/mcp"]
            }
        ]
    };

    private static readonly Dictionary<string, string> _hosted = new()
    {
        ["mcp-vault"] = "http://127.0.0.1:5001/mcp",
        ["mcp-websearch"] = "http://127.0.0.1:5002/mcp"
    };

    [Fact]
    public void Rehosted_AnAgentsEndpoints_PointAtTheStack_AndTheUnhostedOnesAreDropped()
    {
        EvalStack.Rehosted(_shipped, _hosted).Agents.Single().McpServerEndpoints
            .ShouldBe(["http://127.0.0.1:5001/mcp"]);
    }

    [Fact]
    public void Rehosted_AWorkersEndpoints_PointAtTheStackToo()
    {
        // Left at the compose host names, a real worker dials a configured endpoint nothing
        // answers, and one of those fails its whole session.
        EvalStack.Rehosted(_shipped, _hosted).SubAgents.Single().McpServerEndpoints
            .ShouldBe(["http://127.0.0.1:5002/mcp"]);
    }
}