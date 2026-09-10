using Domain.Channels;
using Domain.DTOs.Channel;
using Domain.Prompts;
using Infrastructure.Utils;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Integration.McpServers;

// What every MCP server in the repo must have, however it is built: its own settings available to
// everything it registers, a server, an HTTP transport, and one call-tool filter. Thirteen rows,
// each driving the ConfigModule that ships.
//
// Written as comparisons across servers rather than assertions about one, because the drift this
// pins was invisible precisely because nothing compared them: two servers disagreed about how
// configuration binds, five read a config source they do not have, and seven filters were missing
// the cancellation rule the other six treat as load-bearing.
public class McpServerContractTests
{
    public static TheoryData<string> Servers => McpServerRegistrations.Ids(McpServerRegistrations.All);

    // Everything the module registers reads its settings out of the container, so a server that
    // resolves a second instance would hand two halves of itself two different configurations.
    [Theory]
    [MemberData(nameof(Servers))]
    public void EveryServer_ResolvesItsSettingsAsASingleton(string id)
    {
        var row = McpServerRegistrations.Get(id);
        using var server = new ConfiguredServer(row);

        var settings = server.Provider.GetService(row.Settings.GetType());

        settings.ShouldBeSameAs(row.Settings, $"{id} must register the settings it was handed");
        server.Provider.GetService(row.Settings.GetType())
            .ShouldBeSameAs(settings, $"{id} must register its settings as a singleton");
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public void EveryServer_RegistersTheMcpHost(string id)
    {
        using var server = new ConfiguredServer(McpServerRegistrations.Get(id));

        // Both halves are asserted on the descriptors the two calls add, not by resolving IOptions:
        // an IOptions<T> resolves to a default instance whether or not anyone configured it, so
        // resolving it would stay green against a module that never started a server at all.
        server.Services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(IConfigureOptions<McpServerOptions>),
            $"{id} must start an MCP server");
        server.Services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(IConfigureOptions<HttpServerTransportOptions>),
            $"{id} must add the HTTP transport");
    }

    // Two filters nested around each other is the failure this counts: the outer one converts the
    // very cancellation the inner deliberately rethrows, so a cancelled call comes back as an error
    // result the agent's pump will retry.
    [Theory]
    [MemberData(nameof(Servers))]
    public void EveryServer_HasExactlyOneCallToolFilter(string id)
    {
        using var server = new ConfiguredServer(McpServerRegistrations.Get(id));

        McpServerProbe.CallToolFilterCount(server.Services)
            .ShouldBe(1, $"{id} must have exactly one call-tool filter");
    }

    // The two dual-role servers can raise something with the agent unprompted but have nobody to
    // speak to, so they accept the protocol tools and drop what arrives. That is a stated fact about
    // them, and this row is where it is stated.
    [Theory]
    [MemberData(nameof(DualRoleServers))]
    public void EveryDualRoleServer_AdvertisesTheChannelProtocolTools(string id)
    {
        using var server = new ConfiguredServer(McpServerRegistrations.Get(id));

        var tools = server.Provider.GetServices<McpServerTool>()
            .Select(tool => tool.ProtocolTool.Name).ToList();

        tools.ShouldContain(ChannelProtocol.SendReplyTool, $"{id} must advertise send_reply");
        tools.ShouldContain(ChannelProtocol.RequestApprovalTool, $"{id} must advertise request_approval");
    }

    // One row per skill the manifest declares: the server it names publishes the index and the
    // skill's body. A server that stops shipping a skill its tools are taught by fails here, before
    // an agent finds the advertisement gone.
    [Theory]
    [MemberData(nameof(Skills))]
    public void EveryServer_ServesEverySkillTheManifestSaysItDoes(string skillName)
    {
        var declaration = PromptManifest.FindSkill(skillName)!;
        using var server = new ConfiguredServer(McpServerRegistrations.Get(declaration.ServedBy["mcp-".Length..]));

        var addresses = server.Provider.GetServices<McpServerResource>()
            .Select(resource => resource.ProtocolResourceTemplate.UriTemplate)
            .ToList();

        addresses.ShouldContain(SkillServerResources.IndexAddress,
            $"{declaration.ServedBy} must publish the skills index");
        addresses.ShouldContain(SkillServerResources.BodyAddress(skillName),
            $"{declaration.ServedBy} must publish the body of '{skillName}'");
    }

    public static TheoryData<string> Skills =>
        PromptManifest.Skills.Select(s => s.Name).Aggregate(new TheoryData<string>(), (data, name) =>
        {
            data.Add(name);
            return data;
        });

    public static TheoryData<string> DualRoleServers => McpServerRegistrations.Ids(
        McpServerRegistrations.All.Where(row => row.Role == McpServerRole.DualRole));

    // The Role column decides which rows the channel contract tests run over, so until now a row
    // mislabelled Tool simply dropped out of them and nothing went red. These two tests are the
    // column's own check: a channel row has an inbox and an emitter, a tool row has neither.
    [Theory]
    [MemberData(nameof(ChannelServers))]
    public void EveryChannelServer_RegistersTheInboxAndTheEmitter(string id)
    {
        using var server = new ConfiguredServer(McpServerRegistrations.Get(id));

        server.Services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(ChannelInbox),
            $"{id} is a channel row, so it must register a ChannelInbox");
        server.Services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(ChannelNotificationEmitter),
            $"{id} is a channel row, so it must register a ChannelNotificationEmitter");
    }

    [Theory]
    [MemberData(nameof(ToolOnlyServers))]
    public void EveryToolOnlyServer_RegistersNoChannel(string id)
    {
        using var server = new ConfiguredServer(McpServerRegistrations.Get(id));

        server.Services.ShouldNotContain(
            descriptor => descriptor.ServiceType == typeof(ChannelInbox),
            $"{id} is a tool row, so a ChannelInbox means the row is mislabelled");
        server.Services.ShouldNotContain(
            descriptor => descriptor.ServiceType == typeof(ChannelNotificationEmitter),
            $"{id} is a tool row, so an emitter means the row is mislabelled");
    }

    public static TheoryData<string> ChannelServers =>
        McpServerRegistrations.Ids(McpServerRegistrations.ChannelServers);

    public static TheoryData<string> ToolOnlyServers => McpServerRegistrations.Ids(
        McpServerRegistrations.All.Where(row => row.Role == McpServerRole.Tool));

    // The SignalR module connects its Redis multiplexer and ServiceBus its client eagerly, at
    // Configure time, and registers them as instance singletons — which DI never disposes, in the
    // shipped server or here. There each instance lives exactly as long as the process; here a row
    // is configured once per theory case, so without this wrapper every case would leak a
    // multiplexer whose background reconnect loop runs until the test process exits.
    private sealed class ConfiguredServer : IDisposable
    {
        private ServiceProvider? _provider;

        public ConfiguredServer(McpServerRow row) => row.Configure(Services);

        public ServiceCollection Services { get; } = [];

        public ServiceProvider Provider => _provider ??= Services.BuildServiceProvider();

        public void Dispose()
        {
            _provider?.Dispose();
            foreach (var instance in Services.Select(descriptor => descriptor.ImplementationInstance))
            {
                switch (instance)
                {
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                    case IAsyncDisposable asyncDisposable:
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        break;
                }
            }
        }
    }
}