using Domain.Prompts;
using Infrastructure.Agents;
using Infrastructure.Utils;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.McpServers;

// A skill crosses the wire as two resources in the standard shape — the index and the body — and a
// session reads them back into the manifest's declaration. These drive the real home server's
// registration over real HTTP, and the session builder against it, so what is asserted is what a
// deployment serves and what an agent gets.
public class SkillResourceTests
{
    private static Task<RunningServer> StartHomeAsync() =>
        InMemoryMcpServer.StartAsync(McpServerRegistrations.Get("homeassistant").Configure);

    [Fact]
    public async Task TheHomeServer_ListsTheIndexAndABodyPerSkill()
    {
        await using var server = await StartHomeAsync();

        var resources = await server.Client.ListResourcesAsync(cancellationToken: CancellationToken.None);

        resources.Select(r => r.Uri).ShouldContain(SkillServerResources.IndexAddress);
        resources.Select(r => r.Uri).ShouldContain(SkillServerResources.BodyAddress(HomeWatchesSkill.Name));
        resources.Select(r => r.Uri).ShouldContain(SkillServerResources.BodyAddress(HomeAssistantSkill.Name));
    }

    [Fact]
    public async Task TheIndex_NamesEachSkillAndWhereItsBodyIs()
    {
        await using var server = await StartHomeAsync();

        var index = await ReadAsync(server, SkillServerResources.IndexAddress);

        index.ShouldContain($"\"name\":\"{HomeWatchesSkill.Name}\"");
        index.ShouldContain($"\"location\":\"{SkillServerResources.BodyAddress(HomeWatchesSkill.Name)}\"");
    }

    [Fact]
    public async Task TheBody_CarriesTheDeclaredNameAndDescriptionAsFrontmatter()
    {
        await using var server = await StartHomeAsync();
        var declaration = PromptManifest.FindSkill(HomeWatchesSkill.Name).ShouldNotBeNull();

        var body = await ReadAsync(server, SkillServerResources.BodyAddress(HomeWatchesSkill.Name));

        body.ShouldStartWith("---\n");
        body.ShouldContain($"\nname: {declaration.Name}\n");
        body.ShouldContain($"\ndescription: {declaration.Description}\n");
        body.ShouldContain("watch.json");
    }

    // The session's side: a real client reads both and binds by name, and the skill it exposes is
    // the declaration's — granted by granting the server, with no second list to keep in step.
    [Fact]
    public async Task ASessionAgainstTheHomeServer_ExposesTheSkillBoundToItsDeclaration()
    {
        await using var server = await StartHomeAsync();

        await using var session = await BuildAsync(server.Endpoint);

        session.Skills.Select(s => s.Name).ShouldBe([HomeWatchesSkill.Name, HomeAssistantSkill.Name], ignoreOrder: true);
        var skill = session.Skills.Single(s => s.Name == HomeWatchesSkill.Name);
        skill.Declaration.ShouldBeSameAs(PromptManifest.FindSkill(HomeWatchesSkill.Name));
        skill.Description.ShouldBe(HomeWatchesSkill.Description);
        skill.Body.ShouldBe(HomeWatchesSkill.Body.TrimEnd());
        var home = session.Skills.Single(s => s.Name == HomeAssistantSkill.Name);
        home.Declaration.ShouldBeSameAs(PromptManifest.FindSkill(HomeAssistantSkill.Name));
        home.Body.ShouldBe(HomeAssistantSkill.Body.TrimEnd());
    }

    [Fact]
    public async Task ASessionAgainstAServerWithNoIndex_GetsNoSkillFromIt()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer(options => options.Capabilities = new ServerCapabilities { Resources = new ResourcesCapability() })
            .WithHttpTransport()
            .WithTools<FailingTools>());

        await using var session = await BuildAsync(server.Endpoint);

        session.Skills.ShouldBeEmpty();
    }

    // An outpost, or any server this repo did not budget, may ship a skill: it is offered under the
    // undeclared budgets and marked, the way its prompt would be.
    [Fact]
    public async Task ASkillNoDeclarationNames_IsOfferedUndeclared()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FailingTools>()
            .AddSkills(new SkillText("surprise-skill", "Does surprising things.", "# Surprise\n\nBoo.")));

        await using var session = await BuildAsync(server.Endpoint);

        var skill = session.Skills.ShouldHaveSingleItem();
        skill.Name.ShouldBe("surprise-skill");
        skill.Declaration.Declared.ShouldBeFalse();
        skill.Declaration.BodyBudget.ShouldBe(PromptManifest.UndeclaredSkillBodyBudget);
        skill.Body.ShouldBe("# Surprise\n\nBoo.");
    }

    // A server whose index is not what the reader expects ships no skill this session, and the
    // session still builds: a broken skill is the deployment's problem, not this turn's.
    [Fact]
    public async Task AServerWhoseIndexIsBroken_ContributesNoSkill_AndTheSessionStillBuilds()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FailingTools>()
            .Services.AddSingleton(McpServerResource.Create(
                () => "this is not an index",
                new McpServerResourceCreateOptions
                {
                    UriTemplate = SkillServerResources.IndexAddress,
                    Name = "skills",
                    MimeType = SkillServerResources.IndexMimeType
                })));

        await using var session = await BuildAsync(server.Endpoint);

        session.Skills.ShouldBeEmpty();
        session.ClientManager.Tools.ShouldNotBeEmpty();
    }

    private static Task<ThreadSession> BuildAsync(string endpoint) =>
        ThreadSession.CreateAsync(
            [McpServerEndpoint.Configured(endpoint)], "skills-test", "fran", "skills test",
            [], new HashSet<string>(), null, CancellationToken.None);

    private static async Task<string> ReadAsync(RunningServer server, string uri)
    {
        var content = await server.Client.ReadResourceAsync(uri, cancellationToken: CancellationToken.None);
        return string.Join("", content.Contents.OfType<TextResourceContents>().Select(c => c.Text));
    }
}