using Domain.Agents;
using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.Sugary.Vfs;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerSugary.App;
using McpServerSugary.Channel;
using McpServerSugary.McpPrompts;
using McpServerSugary.McpTools;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureSugary(this IServiceCollection services, SugarySettings settings)
    {
        var homeZone = HomeZone(settings);

        services
            .AddSugaryStore(settings)
            .AddSingleton<ISugaryLog, SqliteSugaryLog>()
            .AddSingleton(sp => new SugaryFileSystem(sp.GetRequiredService<ISugaryLog>(), homeZone))
            .AddHostedService<SugaryDatabaseMigrator>()
            .AddSingleton(settings.Uploads)
            .AddSingleton(settings.Retention)
            .AddSingleton<UploadStore>()
            // What each model accepts, from the catalogue the agent registers on connect: the one
            // thing this channel needs to know about models, to refuse a photo one cannot see.
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<AgentTurns>()
            .AddSingleton<SugaryChannel>()
            .AddSugaryApp(settings);

        services
            .AddToolServer(settings, ToolResponse.Create)
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<FetchAttachmentTool>()
            .WithTools<RegisterAgentsTool>()
            // Gate-on-live: the app waits on its own request, so an estimate nobody is polling for
            // is answered as an error at once rather than buffered for an agent that may never come.
            .AddChannelServer(DeliveryPolicy.GateOnLive)
            .AddFileSystemTools<SugaryFileSystem>()
            .AddFileSystemResource<SugaryFileSystem>()
            .AddSkills(SugarySkill.Text)
            .WithPrompts<McpSystemPrompt>();

        return services;
    }

    // Resolved at registration so an id the container does not know stops the server at startup,
    // before a single time is written on the wrong clock.
    public static TimeZoneInfo HomeZone(SugarySettings settings) =>
        string.IsNullOrWhiteSpace(settings.HomeZone)
            ? TimeZoneInfo.Local
            : TimeZoneInfo.FindSystemTimeZoneById(settings.HomeZone);
}