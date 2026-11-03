using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Commands;
using Accounts.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Domain.Prompts;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The household's agents through the admin API (seam 1 of Tenants 4c): an owner renames an agent and
// edits its custom instructions, sees the versions those replaced and restores one, adds an agent from
// a shipped template and removes one — and nothing else about an agent. The turn side is the agent's
// own reader of the household's configuration against a real Redis, built into the agent a turn runs,
// whose prompt the model is sent; the picker side is the agent-names view the chat client and the
// Telegram bot read.
public sealed class HouseholdAgentsAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string TelegramCredential = "telegram-credential";

    private FakeOpenRouter _model = null!;

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    // A household that saved no agents has the two templates its turns run, jonas answering by default.
    [Fact]
    public async Task AnOwner_ReadsTheHouseholdsAgents_AsItsTurnsRunThem()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");

        var agents = await AgentsAsync(server.Admin(await server.MintSessionAsync(ana)));

        agents.DefaultAgent.ShouldBe(AgentTemplates.Jonas);
        agents.Agents.Select(a => (a.Id, a.Name, a.Template)).ShouldBe(
        [
            ("jonas", AgentTemplates.Named("jonas")!.Name, "jonas"),
            ("sugary", AgentTemplates.Named("sugary")!.Name, "sugary")
        ]);
        agents.Templates.Select(t => t.Name).ShouldBe(AgentTemplates.All.Select(t => t.Id), ignoreOrder: true);
        agents.InstructionsLength.ShouldBe(new AdminSettings().AgentInstructionsLength);
    }

    [Fact]
    public async Task ARenamedAgentWithNewInstructions_AnswersWithThemOnTheNextTurn()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        await using var reader = Reader(server);
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        (await TurnAsync(reader, "jonas", fran.TenantId)).ShouldContain("You are Jonas.");

        var saved = await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit("Chef", "Answer every question with a recipe."));

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await saved.Content.ReadFromJsonAsync<AdminSaved<AdminAgents>>())!;
        answer.Warning.ShouldBeNull();
        answer.Value.Agents.Single(a => a.Id == "jonas").ShouldSatisfyAllConditions(
            a => a.Name.ShouldBe("Chef"),
            a => a.CustomInstructions.ShouldBe("Answer every question with a recipe."));
        answer.Value.Agents.Select(a => a.Id).ShouldBe(["jonas", "sugary"], "the starting agents kept, not just the one edited");
        await Eventually.Until(async () => (await TurnAsync(reader, "jonas", fran.TenantId)).Contains("You are Chef."),
            "the announced save to reach the agent's reader");
        (await TurnAsync(reader, "jonas", fran.TenantId)).ShouldContain("Answer every question with a recipe.");
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(["Changed agent jonas's name, custom instructions"]);
    }

    [Fact]
    public async Task ARestoredVersion_IsWhatTheAgentUsesAfterwards()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var reader = Reader(server);
        var agents = new HouseholdAgentSource(reader, new SharedServers());
        var admin = server.Admin(await server.MintSessionAsync(ana));
        await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit("Chef", "Recipes only."));
        server.Clock.Advance(TimeSpan.FromMinutes(5));
        await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit("Sommelier", "Wine only."));

        var versions = await VersionsAsync(admin, "jonas");

        versions.Select(v => (v.Name, v.CustomInstructions)).ShouldBe([("Chef", "Recipes only."), ("Jonas", null)]);
        versions.ShouldAllBe(v => v.ReplacedBy == "Ana");
        server.Clock.Advance(TimeSpan.FromMinutes(5));
        var restored = await SendAsync(admin, HttpMethod.Post, AdminProtocol.RestoreAgentVersion("jonas", versions[0].Id));

        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.Until(async () => (await agents.ForAsync(household, CancellationToken.None)).Find("jonas")!.Name == "Chef",
            "the restored version to reach the agent's reader");
        (await agents.ForAsync(household, CancellationToken.None)).Find("jonas")!.CustomInstructions.ShouldBe("Recipes only.");
        (await VersionsAsync(admin, "jonas")).Select(v => v.Name).ShouldBe(["Sommelier", "Chef", "Jonas"],
            "a restore replaces a version too, so it can be undone");
    }

    [Fact]
    public async Task OnlyTheLastFewVersions_AreKept()
    {
        await using var server = await StartAsync(admin => admin with { AgentVersionsKept = 2 });
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        foreach (var name in new[] { "One", "Two", "Three" })
        {
            server.Clock.Advance(TimeSpan.FromMinutes(1));
            await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit(name, null));
        }

        (await VersionsAsync(admin, "jonas")).Select(v => v.Name).ShouldBe(["Two", "One"]);
    }

    [Fact]
    public async Task InstructionsOverTheLimit_AreRefused_AndNothingChanges()
    {
        await using var server = await StartAsync(admin => admin with { AgentInstructionsLength = 20 });
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit("Chef", new string('a', 21)));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Problems.ShouldHaveSingleItem().ShouldContain("20");
        (await AgentsAsync(admin)).Agents.Single(a => a.Id == "jonas").Name.ShouldBe("Jonas");
        (await VersionsAsync(admin, "jonas")).ShouldBeEmpty();
        (await ChangesAsync(admin)).ShouldBeEmpty();
        (await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit("Chef", new string('a', 20)))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnAgentWithNoName_IsRefused(string name)
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        (await admin.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit(name, null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(admin, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition("jonas", name))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    // Both read the agent-names view: the chat client's picker (the hub, on the person's assertion for
    // its channel) and Telegram's /agent (on the person's for Telegram's).
    [Fact]
    public async Task AnAgentAddedFromATemplate_IsListedWherePeoplePickTheirAgent()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var telegram = new HouseholdConfigurationClient(
            server.Client(), TelegramCredential, null, redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));
        var proof = HouseholdProof.OfAssertion(server.Service<global::Accounts.Signing.Assertions>().OfGuest(household, "telegram"));
        (await telegram.AgentNamesAsync(household, proof, CancellationToken.None)).Agents.Count.ShouldBe(2);
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var added = await SendAsync(admin, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition("jonas", "Cocina"));

        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agent = (await added.Content.ReadFromJsonAsync<AdminSaved<AdminAgents>>())!.Value.Agents.Where(a => a.Name == "Cocina").ShouldHaveSingleItem();
        agent.Template.ShouldBe("jonas");
        agent.Id.ShouldBe("jonas-2", "jonas is taken");
        await Eventually.Until(async () => (await telegram.AgentNamesAsync(household, proof, CancellationToken.None)).Agents
                .Select(a => (a.Id, a.Name)).SequenceEqual([("jonas", "Jonas"), ("sugary", "Sugary"), ("jonas-2", "Cocina")]),
            "the added agent to be listed");
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe(["Added agent jonas-2 from the jonas template"]);
    }

    // A household starts with no voice agent; one added from the voice template takes the id the
    // voice server addresses, under the name its owner gave it.
    [Fact]
    public async Task AnAgentAddedFromTheVoiceTemplate_TakesTheIdVoiceAddresses_UnderItsOwnersName()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        (await AgentsAsync(admin)).Agents.ShouldNotContain(a => a.Template == AgentTemplates.Nabu);

        var added = await SendAsync(admin, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition(AgentTemplates.Nabu, "Voz"));

        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agent = (await added.Content.ReadFromJsonAsync<AdminSaved<AdminAgents>>())!.Value.Agents
            .Where(a => a.Template == AgentTemplates.Nabu).ShouldHaveSingleItem();
        agent.Id.ShouldBe(AgentTemplates.Nabu);
        agent.Name.ShouldBe("Voz");
    }

    // One agent of a household answers its satellites, and its owner says which: any of its agents, a
    // Jonas as well as a Nabu. Marked none, the household's agent of the voice template does; with none,
    // nobody does, and the page says so. What answers aloud is what the agent's own reader routes to.
    [Fact]
    public async Task AnOwner_MarksWhichAgentAnswersAloud_AndTheAgentRoutesToIt()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var reader = Reader(server);
        var agents = new HouseholdAgentSource(reader, new SharedServers());
        var admin = server.Admin(await server.MintSessionAsync(ana));
        (await AgentsAsync(admin)).ShouldSatisfyAllConditions(
            a => a.VoiceAgent.ShouldBeNull(), a => a.AnswersAloud.ShouldBeNull());

        await SendAsync(admin, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition(AgentTemplates.Nabu, "Voz"));
        (await AgentsAsync(admin)).AnswersAloud.ShouldBe(AgentTemplates.Nabu, "the voice template's agent, marked none");

        var marked = await admin.PutAsync(AdminProtocol.VoiceAgent, new VoiceAgentChoice("jonas"));

        marked.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await marked.Content.ReadFromJsonAsync<AdminSaved<AdminAgents>>())!.Value.ShouldSatisfyAllConditions(
            a => a.VoiceAgent.ShouldBe("jonas"), a => a.AnswersAloud.ShouldBe("jonas"));
        await Eventually.Until(async () => (await agents.ForAsync(household, CancellationToken.None)).VoiceAgent == "jonas",
            "the marked voice agent to reach the agent's reader");
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldContain("Marked agent jonas to answer aloud");

        (await admin.PutAsync(AdminProtocol.VoiceAgent, new VoiceAgentChoice("ghost"))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsync(AdminProtocol.VoiceAgent, new VoiceAgentChoice(null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AgentsAsync(admin)).ShouldSatisfyAllConditions(
            a => a.VoiceAgent.ShouldBeNull(), a => a.AnswersAloud.ShouldBe(AgentTemplates.Nabu));
    }

    // The Sugary estimator answers one meal at a time and holds no conversation, so it cannot answer a
    // satellite (user's decision, pass 9): it is marked so on the list, which the page's select reads,
    // and a mark naming it is refused.
    [Fact]
    public async Task AnAgentWithNoTools_MayNotAnswerAloud()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var listed = await AgentsAsync(admin);
        var refused = await admin.PutAsync(AdminProtocol.VoiceAgent, new VoiceAgentChoice(AgentTemplates.Sugary));

        listed.Agents.Single(a => a.Template == AgentTemplates.Sugary).MayAnswerAloud.ShouldBeFalse();
        listed.Agents.Single(a => a.Template == AgentTemplates.Jonas).MayAnswerAloud.ShouldBeTrue();
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentsAsync(admin)).VoiceAgent.ShouldBeNull();
    }

    // Removing the agent that answers aloud is removing it from the satellites too: the mark goes with
    // it in the same save, and the household's voice falls back as if none were marked.
    [Fact]
    public async Task RemovingTheMarkedVoiceAgent_ClearsTheMark()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));
        await SendAsync(admin, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition("jonas", "Cocina"));
        await admin.PutAsync(AdminProtocol.VoiceAgent, new VoiceAgentChoice("jonas-2"));

        (await SendAsync(admin, HttpMethod.Delete, AdminProtocol.Agent("jonas-2"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AgentsAsync(admin)).ShouldSatisfyAllConditions(
            a => a.VoiceAgent.ShouldBeNull(), a => a.AnswersAloud.ShouldBeNull());
    }

    [Fact]
    public async Task ATemplateThisDeploymentDoesNotShip_IsRefused()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var refused = await SendAsync(admin, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition("butler", "Alfred"));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AgentsAsync(admin)).Agents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ARemovedAgent_IsNoLongerTheHouseholds_AndTakesItsVersionsWithIt()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        await using var reader = Reader(server);
        var agents = new HouseholdAgentSource(reader, new SharedServers());
        var admin = server.Admin(await server.MintSessionAsync(ana));
        await SendAsync(admin, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition("jonas", "Cocina"));
        await admin.PutAsync(AdminProtocol.Agent("jonas-2"), new AgentEdit("Cocinera", null));
        (await agents.ForAsync(household, CancellationToken.None)).Find("jonas-2").ShouldNotBeNull();

        var removed = await SendAsync(admin, HttpMethod.Delete, AdminProtocol.Agent("jonas-2"));

        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.Until(async () => (await agents.ForAsync(household, CancellationToken.None)).Find("jonas-2") is null,
            "the removal to reach the agent's reader");
        await SendAsync(admin, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition("jonas", "Otra"));
        (await VersionsAsync(admin, "jonas-2")).ShouldBeEmpty("a new agent under a freed id starts with no history");
    }

    // A household always has an agent that answers when nobody named one: the default stays, and the
    // last agent too, since a section with none would be read as the two starting templates again.
    [Fact]
    public async Task TheDefaultAgentAndTheLastAgent_AreNotRemoved_AndSayWhy()
    {
        await using var server = await StartAsync();
        var (household, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var theDefault = await SendAsync(admin, HttpMethod.Delete, AdminProtocol.Agent("jonas"));

        theDefault.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await theDefault.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("by default");
        await SetAsync(server, household, HouseholdSections.Agents,
            """{ "agents": [ { "id": "chef", "template": "jonas", "name": "Chef" } ] }""");
        var theLast = await SendAsync(admin, HttpMethod.Delete, AdminProtocol.Agent("chef"));
        theLast.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await theLast.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("at least one agent");
        (await AgentsAsync(admin)).Agents.Select(a => a.Id).ShouldBe(["chef"]);
        (await ChangesAsync(admin)).ShouldBeEmpty();
        (await SendAsync(admin, HttpMethod.Delete, AdminProtocol.Agent("nobody"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A seeded definition — the home's own agents — carries its name and instructions in itself, so
    // that is where an owner's edit goes; its model, servers and sections stay as seeded, whatever
    // else the edit carries.
    [Fact]
    public async Task ASeededDefinition_IsRenamedInItself_AndNothingElseAboutItChanges()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var nabu = new AgentDefinition
        {
            Id = "nabu", Name = "Nabu", Model = "z-ai/glm-5.3", McpServerEndpoints = ["http://mcp-voice:8080/mcp"],
            PromptSections = ["voice"], CustomInstructions = "Speak softly."
        };
        await SetAsync(server, fran.TenantId, HouseholdSections.Agents,
            HouseholdSections.AgentsSchema.Write(new AgentsSection
            {
                DefaultAgent = "nabu", Agents = [new HouseholdAgent { Id = "nabu", Definition = nabu }]
            }).ToJsonString());
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var saved = await admin.PutAsync(AdminProtocol.Agent("nabu"),
            new { name = "Nabucco", customInstructions = "Sing.", model = "openai/gpt-6-luna", prompt_sections = new string[0] });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (_, printed) = await server.RunAsync(AccountsCommand.GetHouseholdConfig, "--household", fran.TenantId, "--section", HouseholdSections.Agents);
        var entry = HouseholdSections.AgentsSchema.Read(JsonNode.Parse(printed)!.AsObject()).Agents.ShouldHaveSingleItem();
        entry.Name.ShouldBeNull();
        entry.CustomInstructions.ShouldBeNull();
        entry.Definition.ShouldNotBeNull().ShouldSatisfyAllConditions(
            d => d.Name.ShouldBe("Nabucco"),
            d => d.CustomInstructions.ShouldBe("Sing."),
            d => d.Model.ShouldBe(nabu.Model),
            d => d.McpServerEndpoints.ShouldBe(nabu.McpServerEndpoints),
            d => d.PromptSections.ShouldBe(nabu.PromptSections));
        (await VersionsAsync(admin, "nabu")).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            v => v.Name.ShouldBe("Nabu"), v => v.CustomInstructions.ShouldBe("Speak softly."));
    }

    [Fact]
    public async Task AMember_IsRefusedEveryAgentsRoute_AndChangesNothing()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");
        var laura = server.Admin(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));

        (await laura.GetAsync(AdminProtocol.Agents)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.PutAsync(AdminProtocol.Agent("jonas"), new AgentEdit("Chef", null))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(laura, HttpMethod.Post, AdminProtocol.Agents, new AgentAddition("jonas", "Chef"))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(laura, HttpMethod.Delete, AdminProtocol.Agent("sugary"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await laura.GetAsync(AdminProtocol.AgentVersions("jonas"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (_, printed) = await server.RunAsync(AccountsCommand.GetHouseholdConfig, "--household", "home", "--section", HouseholdSections.Agents);
        HouseholdSections.AgentsSchema.Read(JsonNode.Parse(printed)!.AsObject()).Agents.ShouldBeEmpty();
    }

    private Task<AccountsServer> StartAsync(Func<AdminSettings, AdminSettings>? admin = null) =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential, ["mcp-channel-telegram"] = TelegramCredential },
            Redis = new() { ConnectionString = redis.ConnectionString },
            Admin = admin?.Invoke(s.Admin) ?? s.Admin
        });

    // The agent's own reader of the household's configuration, keeping what it read for longer than
    // any test runs: only a save's announcement makes it read again.
    private HouseholdConfigurationClient Reader(AccountsServer server) =>
        new(server.Client(), AgentCredential, HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens),
            redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));

    // A turn of the household's agent as the agent builds it — its definition from the reader, through
    // the real factory and its OpenRouter client against a fake model — answered with the system prompt
    // the model was sent.
    private async Task<string> TurnAsync(IHouseholdConfiguration reader, string agentId, string household)
    {
        var factory = new MultiAgentFactory(
            new ServiceCollection().AddSingleton(Mock.Of<IThreadStateStore>()).BuildServiceProvider(),
            new AgentDefinitionProvider(new HouseholdAgentSource(reader, new SharedServers()), new CustomAgentRegistry()),
            new OpenRouterConfig { ApiUrl = _model.ApiUrl, ApiKey = "deployment-key" },
            new DomainToolRegistry([]));
        await using var agent = await factory.CreateAsync(
            new AgentKey($"conv-{Guid.NewGuid():N}", agentId), "fran", agentId, Mock.Of<IToolApprovalHandler>(), tenantId: household);
        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hello")]).ToListAsync();
        return _model.Requests[^1].SystemPrompt.ShouldNotBeNull();
    }

    private static async Task<AdminAgents> AgentsAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.Agents)).Content.ReadFromJsonAsync<AdminAgents>())!;

    private static async Task<IReadOnlyList<AdminAgentVersion>> VersionsAsync(AccountsServer.AdminClient admin, string agentId) =>
        (await (await admin.GetAsync(AdminProtocol.AgentVersions(agentId))).Content.ReadFromJsonAsync<List<AdminAgentVersion>>())!;

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;

    private static Task<HttpResponseMessage> SendAsync(
        AccountsServer.AdminClient admin, HttpMethod method, string path, object? body = null) =>
        admin.SendAsync(new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) });

    private static async Task SetAsync(AccountsServer server, string household, string section, string document)
    {
        var (exit, output) = await server.RunAsync(server.Settings, document,
            AccountsCommand.SetHouseholdConfig, "--household", household, "--section", section, "--file", "-");
        exit.ShouldBe(0, output);
    }
}