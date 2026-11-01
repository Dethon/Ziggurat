using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Prompts;
using Domain.Security.Signing;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpSchedulingServerTests(McpSchedulingServerFixture fixture) : IClassFixture<McpSchedulingServerFixture>
{
    private async Task<McpClient> ConnectAsync() =>
        await McpClient.CreateAsync(
            McpTestSecret.Transport(fixture.McpEndpoint),
            cancellationToken: CancellationToken.None);

    // A schedule is kept under the household of the call that writes it, so a write is a person's turn,
    // signed as the agent signs one.
    private static IFileSystemBackend AsAPerson(McpClient client) =>
        new McpFileSystemBackend(client, "schedules", null).As(new ConversationContext(
            "jonas", "conv-1", "u-1", new ReplyTarget(ChannelIds.SignalR, "conv-1"),
            Principal: new Principal(ChannelIds.SignalR, "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f",
                new Account("4f1c9a7e2b6d4e0a9c3b8d5e7f1a2c4b", "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f")),
            Identity: IdentityClaims.Person(
                "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f", "4f1c9a7e2b6d4e0a9c3b8d5e7f1a2c4b", ChannelIds.SignalR, ["member"])));

    [Fact]
    public async Task McpServer_ReadFilesystemResource_ReturnsMetadata()
    {
        var client = await ConnectAsync();

        var content = await client.ReadResourceAsync("filesystem://schedules");
        var text = string.Join("", content.Contents
            .OfType<TextResourceContents>()
            .Select(c => c.Text));

        text.ShouldContain("\"name\":\"schedules\"");
        text.ShouldContain("\"mountPoint\":\"/schedules\"");

        await client.DisposeAsync();
    }

    // The prompt is the stub — which mechanism a request is — and the file's rules are the skill
    // the same server serves as a resource.
    [Fact]
    public async Task McpServer_GetSchedulingPrompt_KeepsTheMechanismAndLeavesTheFileToTheSkill()
    {
        var client = await ConnectAsync();

        var result = await client.GetPromptAsync("scheduling_prompt");
        var text = string.Join("", result.Messages
            .Select(m => m.Content)
            .OfType<TextContentBlock>()
            .Select(c => c.Text));
        var skill = await client.ReadResourceAsync(
            SkillServerResources.BodyAddress(SchedulingSkill.Name), cancellationToken: CancellationToken.None);
        var body = string.Join("", skill.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        text.ShouldContain("not an alarm clock");
        text.ShouldContain(SchedulingSkill.Name);
        body.ShouldContain("schedule.json");
        body.ShouldContain("0 9 * * *");

        await client.DisposeAsync();
    }

    // The prompt is fetched for nobody and cached for every household, so it names no agent: the
    // catalogue the agent registers is the home's, and was shown to every household from here.
    [Fact]
    public async Task McpServer_GetSchedulingPrompt_NamesNoRegisteredAgent()
    {
        var client = await ConnectAsync();

        var register = await client.CallToolAsync(
            ChannelProtocol.RegisterAgentsTool,
            ChannelProtocol.ToArguments(new RegisterAgentsParams
            {
                Agents =
                [
                    new AgentCatalogEntry("jonas", "Jonas", "test agent"),
                    new AgentCatalogEntry("itest-summary-agent", "Summary Agent", "Used by the snippet test."),
                ]
            }),
            cancellationToken: CancellationToken.None);
        (register.IsError ?? false).ShouldBeFalse();

        var result = await client.GetPromptAsync("scheduling_prompt");
        var text = string.Join("", result.Messages
            .Select(m => m.Content)
            .OfType<TextContentBlock>()
            .Select(c => c.Text));

        text.ShouldBe(SchedulingPrompt.Prompt);
        text.ShouldNotContain("itest-summary-agent");

        await client.DisposeAsync();
    }

    // Registering the home's catalogue gives no household a directory: a call's agents are its own
    // household's (the fixture's every household runs jonas alone), so an agent only the catalogue
    // names is refused, and the household's own is written to.
    [Fact]
    public async Task RegisterAgents_GivesNoHouseholdAnAgentItDoesNotRun()
    {
        var client = await ConnectAsync();

        var register = await client.CallToolAsync(
            ChannelProtocol.RegisterAgentsTool,
            ChannelProtocol.ToArguments(new RegisterAgentsParams
            {
                Agents = [new AgentCatalogEntry("jonas", "Jonas", "general"), new AgentCatalogEntry("jack", "Jack", "downloads")]
            }),
            cancellationToken: CancellationToken.None);

        (register.IsError ?? false).ShouldBeFalse();

        (await AsAPerson(client).CreateAsync("/jack/itest-register/schedule.json",
                """{"prompt":"do the thing","cron":"0 7 * * *"}""", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        (await AsAPerson(client).ReadAsync("/jonas/agent_info.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("Jonas");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task FsGlob_WithUnknownFilesystemArgument_IsIgnoredByServer()
    {
        var client = await ConnectAsync();

        var result = await client.CallToolAsync("fs_glob", new Dictionary<string, object?>
        {
            ["pattern"] = "*",
            ["basePath"] = "/",
            ["filesystem"] = "schedules"
        });

        result.IsError.ShouldNotBe(true);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CreateGlobRead_RoundTrip_PersistsAndReadsSchedule()
    {
        var client = await ConnectAsync();

        var person = AsAPerson(client);

        (await person.CreateAsync("/jonas/itest-news/schedule.json",
                """{"prompt":"summarize the integration news","cron":"0 8 * * *"}""", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        (await person.GlobAsync("/jonas", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries.ShouldContain("/jonas/itest-news/");

        (await person.ReadAsync("/jonas/itest-news/schedule.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("summarize the integration news");

        await client.DisposeAsync();
    }
}