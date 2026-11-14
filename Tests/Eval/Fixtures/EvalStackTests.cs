using Agent.Settings;
using Domain.DTOs;
using Shouldly;

namespace Tests.Eval.Fixtures;

// The one thing a stack changes about the shipped definition: where its servers are. No model
// and no container — what is pinned is that a worker is pointed at the stack as its parent is,
// because a worker that really runs dials what its own profile names.
public class EvalStackTests
{
    private static readonly TestAgentSettings _shipped = new()
    {
        OpenRouter = new OpenRouterConfiguration { ApiUrl = "https://openrouter.ai/api/v1/", ApiKey = "" },
        Redis = new RedisConfiguration { ConnectionString = "localhost" },
        BackCalls = new() { LemonadeUrl = "http://lemonade:13305" },
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

    // The deployment's shared servers are named apart from any agent, and the agent refuses to start
    // naming one it holds no credential for: each points at the stack where the stack hosts it, and one
    // it does not host is not named at all.
    [Fact]
    public void Rehosted_TheSharedServers_PointAtTheStack_AndTheUnhostedOnesAreDropped()
    {
        var shipped = _shipped with
        {
            SharedServers = new SharedServers
            {
                Scheduling = "http://mcp-scheduling:8080/mcp", Idealista = "http://mcp-idealista:8080/mcp"
            }
        };
        var hosted = new Dictionary<string, string>(_hosted) { ["mcp-scheduling"] = "http://127.0.0.1:5003/mcp" };

        EvalStack.Rehosted(shipped, hosted).SharedServers
            .ShouldBe(new SharedServers { Scheduling = "http://127.0.0.1:5003/mcp" });
    }

    [Fact]
    public void Alone_TheScenariosAgent_LosesItsWorkers_AndNobodyElseDoes()
    {
        // The one departure from the shipped definition a scenario may ask for, and it asks by
        // name: a rung that measures what the model researches by itself cannot leave the
        // delegation to the model's own coin.
        var settings = _shipped with
        {
            Agents =
            [
                _shipped.Agents[0] with { EnabledFeatures = ["filesystem", "subagents", "memory"] },
                _shipped.Agents[0] with { Id = "nabu", EnabledFeatures = ["subagents"] }
            ]
        };

        var alone = EvalStack.Alone(settings, "jonas");

        alone.Agents.Single(a => a.Id == "jonas").EnabledFeatures.ShouldBe(["filesystem", "memory"]);
        alone.Agents.Single(a => a.Id == "nabu").EnabledFeatures.ShouldBe(["subagents"]);
    }

    [Fact]
    public void Rehosted_AWorkersEndpoints_PointAtTheStackToo()
    {
        // Left at the compose host names, a real worker dials a configured endpoint nothing
        // answers, and one of those fails its whole session.
        EvalStack.Rehosted(_shipped, _hosted).SubAgents.Single().McpServerEndpoints
            .ShouldBe(["http://127.0.0.1:5002/mcp"]);
    }

    // A household's own server is named rather than addressed, and the stack runs no tunnel: the name
    // stays in the definition while the stack hosts the server standing in for it, and is resolved
    // to that server, at its URL, when the session is built.
    [Fact]
    public void Rehosted_AHouseholdServerNamedByItsName_StaysNamedWhileTheStackHostsIt()
    {
        var named = _shipped with
        {
            Agents = [_shipped.Agents[0] with { McpServerEndpoints = ["vault", "library"] }]
        };

        EvalStack.Rehosted(named, _hosted).Agents.Single().McpServerEndpoints.ShouldBe(["vault"]);
    }

    [Fact]
    public void OwnServers_ResolveAHouseholdServerToTheServerTheStackHostsInItsPlace()
    {
        var named = _shipped with
        {
            Agents = [_shipped.Agents[0] with { McpServerEndpoints = ["vault"] }],
            SubAgents = [_shipped.SubAgents[0] with { McpServerEndpoints = ["websearch"] }]
        };

        var servers = EvalStack.OwnServers(named, _hosted);

        servers.Resolve("any-household", "vault")!.Address.ShouldBe("http://127.0.0.1:5001/mcp");
        servers.Resolve(null, "websearch")!.Address.ShouldBe("http://127.0.0.1:5002/mcp");
        servers.Resolve("any-household", "library").ShouldBeNull();
    }
}
// A stack stands up what the run can dial and nothing else. A server the agent's definition does
// not name was always dropped from its endpoints, so starting it bought nothing — and for the
// sugary agent, whose one server is Sugary's, it was a sandbox container per benchmark meal.
public class EvalStackDialledTests
{
    private static readonly TestAgentSettings _shipped = new()
    {
        OpenRouter = new OpenRouterConfiguration { ApiUrl = "https://openrouter.ai/api/v1/", ApiKey = "" },
        Redis = new RedisConfiguration { ConnectionString = "localhost" },
        BackCalls = new() { LemonadeUrl = "http://lemonade:13305" },
        Agents =
        [
            new AgentDefinition
            {
                Id = "jonas", Name = "Jonas", Model = "m",
                McpServerEndpoints = ["http://mcp-vault:8080/mcp", "http://mcp-timers:8080/mcp"],
                EnabledFeatures = ["subagents"]
            },
            new AgentDefinition
            {
                Id = "sugary", Name = "Sugary", Model = "m",
                McpServerEndpoints = ["http://mcp-sugary:8080/mcp"]
            }
        ],
        SubAgents =
        [
            new SubAgentDefinition
            {
                Id = "jonas-worker", Name = "Jonas Worker",
                McpServerEndpoints = ["http://mcp-websearch:8080/mcp", "http://mcp-sandbox:8080/mcp"]
            }
        ]
    };

    [Fact]
    public void AnAgentWithWorkers_DialsItsOwnServersAndTheirs()
    {
        EvalStack.Dialled(_shipped, "jonas")
            .ShouldBe(["mcp-vault", "mcp-timers", "mcp-websearch", "mcp-sandbox"], ignoreOrder: true);
    }

    [Fact]
    public void AnAgentWithoutWorkers_DialsOnlyItsOwn()
    {
        EvalStack.Dialled(_shipped, "sugary").ShouldBe(["mcp-sugary"]);
    }

    // A household server named by its name is the compose server the deployment ran in its place.
    [Fact]
    public void AnAgentNamingAHouseholdServer_DialsTheServerThatStandsInForIt()
    {
        var named = _shipped with
        {
            Agents = [_shipped.Agents[1] with { McpServerEndpoints = ["vault", "http://mcp-sugary:8080/mcp"] }]
        };

        EvalStack.Dialled(named, "sugary").ShouldBe(["mcp-vault", "mcp-sugary"]);
    }

    // The definition a scenario runs is the shipped one: the stacks' servers are chosen from it,
    // so the sugary agent's runs stand up Sugary's server and nothing a turn of it could not reach.
    [Fact]
    public void TheShippedSugaryAgent_DialsSugarysServerAlone()
    {
        EvalStack.Dialled(ShippedDefinition.Repository.Settings, "sugary").ShouldBe([EvalStack.Sugary]);
    }
}