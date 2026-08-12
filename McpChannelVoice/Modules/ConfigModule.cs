using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Clients.Transcription;
using Infrastructure.Metrics;
using Mcp.Hosting;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace McpChannelVoice.Modules;

public static class ConfigModule
{
    // Voice is the one server whose settings need a second pass after binding: a satellite with no
    // room or locality of its own inherits the hub's. Program.cs stays six lines of ceremony.
    public static VoiceSettings GetVoiceSettings(this IConfigurationBuilder configBuilder) =>
        configBuilder.BindSettings<VoiceSettings>().WithResolvedLocalityDefaults();

    public static IServiceCollection ConfigureVoiceChannel(
        this IServiceCollection services,
        VoiceSettings settings)
    {
        var redisConnection = settings.RedisConnectionString;

        services
            .AddSingleton(new SatelliteRegistry(settings.Satellites))
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection))
            .AddMetricsPublishing("mcp-channel-voice")
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton(TimeProvider.System)
            .AddSingleton<Domain.Contracts.IThreadStateStore>(sp =>
                new Infrastructure.StateManagers.RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    // The shared retention defaults: this channel writes conversations and never
                    // reads the list, so it needs the horizons and none of the rest.
                    new Domain.DTOs.RetentionSettings(),
                    sp.GetRequiredService<TimeProvider>()))
            .AddSingleton<Domain.Contracts.IConversationFactory, Infrastructure.Conversations.ConversationFactory>();

        services
            .AddSingleton<SatelliteSessionRegistry>()
            .AddSingleton(new VoiceCommandMatcher(settings.Commands))
            .AddSingleton<ILocalCommandHandler, SpeakerVolumeCommandHandler>()
            .AddSingleton<LocalCommandDispatcher>()
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<VoiceConversationManager>(),
                sp.GetRequiredService<LocalCommandDispatcher>(),
                sp.GetRequiredService<ReplyTextAccumulator>(),
                avgLogProbThreshold: settings.Stt.OpenAi.AvgLogProbThreshold,
                noSpeechProbThreshold: settings.Stt.OpenAi.NoSpeechProbThreshold,
                shortSpeechAvgLogProbThreshold: settings.Stt.OpenAi.ShortSpeechAvgLogProbThreshold,
                fullThresholdSpeechMs: settings.Stt.OpenAi.FullThresholdSpeechMs,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()))
            .AddSingleton(sp => new VoiceConversationManager(
                sp.GetRequiredService<Domain.Contracts.IConversationFactory>(),
                sp.GetRequiredService<ReplyTextAccumulator>(),
                sp.GetRequiredService<TimeProvider>(),
                settings.ConversationLifetime,
                sp.GetRequiredService<ILogger<VoiceConversationManager>>()))
            .AddSingleton(sp => new VoiceDeliveryRegistry(
                sp.GetRequiredService<TimeProvider>(),
                settings.ConversationLifetime,
                sp.GetRequiredService<ReplyTextAccumulator>(),
                sp.GetRequiredService<ILogger<VoiceDeliveryRegistry>>()));

        // Streaming TTS reads can outlive the default 100 s client timeout on long replies;
        // cancellation is driven by the per-turn CancellationToken instead (STT self-bounds via
        // RequestTimeout).
        services.AddHttpClient(LemonadeHttp.ClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        services.AddLemonadeTranscription(settings.Stt.OpenAi.ToTranscriptionClientConfig());

        services.AddSingleton<Services.Tse.ITseExtractorClient>(sp =>
            new Services.Tse.TseExtractorClient(
                // No HttpClient.Timeout: the client arms its own deadline from Tse.TimeoutMs via a
                // linked token, so the framework's 100s default must not silently cap it — an owner
                // raising TimeoutMs above 100s would otherwise get a misreported sidecar failure.
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                settings.Tse,
                sp.GetRequiredService<ILogger<Services.Tse.TseExtractorClient>>()));
        services.AddSingleton(sp => new Services.Tse.TseAuditTrail(
            settings.Tse.AuditDir,
            settings.Tse.AuditMaxPairs,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Services.Tse.TseAuditTrail>>()));

        services.AddSingleton<ISpeechToText>(sp =>
        {
            var sttLogger = sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.OpenAiSpeechToText>>();
            var overBudget = McpChannelVoice.Services.Stt.WhisperPromptBuilder.OverBudgetPromptSources(settings);
            if (overBudget.Count > 0)
            {
                sttLogger.LogWarning(
                    "Whisper prompt template(s) longer than MaxPromptChars={MaxChars} are posted whole, "
                    + "and whisper.cpp truncates keeping the tail — the front of the vocabulary is lost: {Sources}",
                    settings.Stt.OpenAi.MaxPromptChars, string.Join(", ", overBudget));
            }

            var inner = new McpChannelVoice.Services.Stt.OpenAiSpeechToText(
                sp.GetRequiredService<Domain.Contracts.IAudioTranscriber>(),
                settings.Stt.OpenAi);

            var segmented = McpChannelVoice.Services.Stt.SegmentedSpeechToText.Wrap(
                inner, settings.Stt.Streaming, settings.WyomingClient, sp.GetRequiredService<ILoggerFactory>());
            return Services.Tse.TseSpeechToText.Wrap(
                segmented,
                settings.Tse,
                sp.GetRequiredService<Services.Tse.ITseExtractorClient>(),
                sp.GetRequiredService<Services.Tse.TseAuditTrail>(),
                sp.GetRequiredService<Domain.Contracts.IMetricsPublisher>(),
                sp.GetRequiredService<ILoggerFactory>());
        });

        services.AddSingleton<ISpeakerVerifier>(sp =>
            new SpeakerVerifier(
                settings.SpeakerVerification,
                () =>
                {
                    var embedder = new OnnxSpeakerEmbedder(settings.SpeakerVerification.ModelPath);
                    var profiles = new SpeakerProfileStore(
                        settings.SpeakerVerification.VoicesPath,
                        embedder,
                        sp.GetRequiredService<ILogger<SpeakerProfileStore>>()).Load();
                    return (embedder, profiles);
                },
                sp.GetRequiredService<ILogger<SpeakerVerifier>>()));

        services.AddHostedService<WyomingSatelliteHost>();
        services.AddSingleton(settings.WyomingClient);
        // One per process: it owns the per-satellite room-noise memory, which is deliberately keyed
        // by satellite so it outlives any single connection.
        services.AddSingleton<SilenceGateFactory>();
        services.AddSingleton(settings.Arbitration);

        services.AddSingleton<ReplyTextAccumulator>();
        services.AddSingleton<ReplySpeaker>();

        services.AddSingleton<ITextToSpeech>(sp =>
            McpChannelVoice.Services.Tts.SilenceTrimmingTextToSpeech.Wrap(
                new McpChannelVoice.Services.Tts.OpenAiTextToSpeech(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    settings.Tts.OpenAi,
                    sp.GetRequiredService<ILogger<McpChannelVoice.Services.Tts.OpenAiTextToSpeech>>()),
                settings.Tts.OpenAi.TrailingSilenceTrimThreshold));

        services.AddSingleton(settings.Announce);
        services.AddSingleton<AnnouncementService>();
        services.AddSingleton<ActiveAlertRegistry>();
        services.AddSingleton<WakeArbiter>();
        services.AddHttpClient();
        services.AddSingleton<InsistentAnnouncementController>();

        services
            .AddMcpHost(settings)
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            .WithTools<CreateConversationTool>()
            // Broadcast: a subscriber that is idle but not yet pruned still receives, so a brief
            // agent gap does not lose an utterance the user would otherwise have to repeat.
            .AddChannelServer(DeliveryPolicy.Broadcast);

        return services;
    }
}