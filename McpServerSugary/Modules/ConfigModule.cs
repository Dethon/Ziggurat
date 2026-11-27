using Domain.Agents;
using Domain.Contracts;
using Domain.Prompts;
using Domain.Security.Sealing;
using Domain.Tools.Sugary.Vfs;
using Domain.Tools.Sugary.Watches;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Push;
using Infrastructure.Clients.Voice;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerSugary.App;
using McpServerSugary.Channel;
using McpServerSugary.Clips;
using McpServerSugary.Glucose;
using McpServerSugary.McpPrompts;
using McpServerSugary.Reminders;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using McpServerSugary.Tables;
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
            .AddSingleton<OneLogWriteAtATime>()
            // Her log's tables are queried on DuckDB, in this server's image alone.
            .AddSingleton<ITableEngine, DuckDbTableEngine>()
            // A file read in chunks is one version for the whole read, though a reading lands a minute.
            .AddSingleton<IReadSnapshots>(sp => new ReadSnapshots(sp.GetRequiredService<TimeProvider>()))
            .AddSingleton(sp => new SugaryFileSystem(
                sp.GetRequiredService<ISugaryLog>(), homeZone, sp.GetRequiredService<ITableEngine>(),
                sp.GetRequiredService<IReadSnapshots>()))
            // Her glucose watches, at /watchers beside the home's, kept for the household.
            .AddSingleton<IGlucoseWatches, SqliteGlucoseWatches>()
            .AddSingleton(sp => new SugaryWatchFileSystem(
                sp.GetRequiredService<IGlucoseWatches>(), sp.GetRequiredService<IHouseholdVoices>(), homeZone,
                sp.GetRequiredService<TimeProvider>()))
            .AddSingleton(sp => new HomeCalendar(sp.GetRequiredService<TimeProvider>(), homeZone))
            .AddHostedService<SugaryDatabaseMigrator>()
            // Her glucose, from the LibreLinkUp follower account she links: one client, keeping each
            // account's token for as long as it lasts.
            .AddLibreLinkUp(LibreLinkUpRegions.Abbott)
            .AddSingleton(new GlucoseCredentials(SealingKey.Parse(settings.SealingKey, "SealingKey")))
            // Every live link read once a period; her glucose watches are told after each poll.
            .AddSingleton(settings.Glucose)
            // A watch's prompts go to the agent on this server's channel; its announcements ring through
            // the announce seam, which rings nowhere until it is wired.
            .AddSingleton(settings.Delivery)
            .AddSingleton<IGlucoseWatchEffects, GlucoseWatchEffects>()
            // Which households turn Sugary off, for the poller and the engine, which hold no proof of one.
            .AddSingleton<SugaryOff>()
            .AddSingleton<GlucoseWatchEngine>()
            .AddSingleton<IGlucosePollObserver>(sp => sp.GetRequiredService<GlucoseWatchEngine>())
            .AddSingleton<GlucosePoller>()
            .AddSingleton(settings.Uploads)
            .AddSingleton(settings.Retention)
            // A meal's clip is read and made small with ffmpeg, in this server's image alone.
            .AddSingleton(settings.Clips)
            .AddSingleton<ClipTools>()
            .AddSingleton<ClipProcessor>()
            // What each model accepts, from the catalogue the agent registers on connect: the one
            // thing this channel needs to know about models, to refuse a photo one cannot see.
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            // The sugary agent as the person's household runs it, on the models the agent registered.
            .AddSingleton<HouseholdAgentCatalogue>()
            .AddSingleton<OpenEstimates>()
            .AddSingleton<SugaryChannel>()
            .AddSugaryApp(settings)
            .AddAssertionVerifier(settings.Accounts.JwksUrl, settings.Mcp.TokenVerification)
            // The household's settings, for the clock its people's times are read on: asked with this
            // server's own credential over a call's token or a request's assertion. It has no Redis to
            // hear a save on, so a saved zone reaches it when what it kept runs out.
            .AddHouseholdConfiguration(settings.Accounts, settings.Mcp.SharedSecret)
            // Whether the session that subscribed a phone still lives, before a pen reminder is sent.
            .AddAccountResolver(settings.Accounts, settings.Mcp.SharedSecret)
            // A glucose alarm rings on the household's satellites through its own voice server,
            // reached through the relay as the timers server reaches it.
            .AddHouseholdVoices(settings.VoiceHub.BaseUrl, settings.Mcp.Audience, settings.Mcp.SharedSecret, announceToken: "")
            .AddSingleton<IGlucoseAnnounceEffect, GlucoseAnnounce>();

        // Pen reminders are pushed with the deployment's VAPID keys, and without them there are none.
        if (settings.WebPush.IsConfigured)
        {
            services.AddHttpClient("WebPush");
            services
                .AddSingleton<IPushMessageSender>(sp => new ModernWebPushSender(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("WebPush"),
                    settings.WebPush.PublicKey!, settings.WebPush.PrivateKey!, settings.WebPush.Subject!))
                .AddSingleton<ReminderPhones>()
                .AddSingleton<PenReminders>()
                .AddSingleton<PeriodReminders>()
                .AddHostedService<PenReminderLoop>();
        }

        // With or without a sealing key: without one no password opens, and the poller says so on every
        // link (CredentialsUnreadable) rather than leaving her link looking well and her watches on a
        // failing link quiet. It asks LibreLinkUp nothing then, and nothing at all while nobody is linked.
        services
            .AddHostedService<GlucosePollLoop>()
            .AddHostedService<GlucoseWatchLoop>();

        services
            .AddToolServer(settings, ToolResponse.Create)
            // Her app waits on each estimate's whole reply, and nobody there can approve a call.
            .AddAgentTurns()
            // A meal's photos and clip, kept until the agent has fetched them for every turn that may.
            .AddUploadStore(settings.Uploads, settings.Retention)
            .WithTools<RegisterAgentsTool>()
            // Gate-on-live: the app waits on its own request, so an estimate nobody is polling for
            // is answered as an error at once rather than buffered for an agent that may never come.
            .AddChannelServer(DeliveryPolicy.GateOnLive)
            // A glucose alarm with no satellite to ring on rings on its person's channels, which only
            // the agent holds: raised in this inbox, settled here when it ended on a channel.
            .AddChannelAlerts()
            // The glucose watches' delegations, for the agent's redelegate and retire-signing-keys.
            .AddDelegationStore<GlucoseWatchDelegationStore>()
            .AddFileSystemTools<SugaryFileSystem>()
            .AddFileSystemResource<SugaryFileSystem>()
            // After the log, so a call naming no filesystem still reaches the log.
            .AddFileSystemTools<SugaryWatchFileSystem>()
            .AddFileSystemResource<SugaryWatchFileSystem>()
            .AddSkills(SugarySkill.Text, GlucoseWatchesSkill.Text)
            .WithPrompts<McpSystemPrompt>();

        return services;
    }

    // The server's own zone: a household's whose zone this host cannot place, and the pen reminders',
    // which no household's call raises. Resolved at registration so an id the container does not
    // know stops the server at startup, before a single time is written on the wrong clock.
    public static TimeZoneInfo HomeZone(SugarySettings settings) =>
        string.IsNullOrWhiteSpace(settings.HomeZone)
            ? TimeZoneInfo.Local
            : TimeZoneInfo.FindSystemTimeZoneById(settings.HomeZone);
}