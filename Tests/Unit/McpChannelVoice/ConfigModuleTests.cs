using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Topics;
using McpChannelVoice.Modules;
using McpChannelVoice.Services;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ConfigModuleTests
{
    // Registration-only smoke: nothing here starts hosted services — resolving the STT/TTS graph
    // must work from settings alone. The STT graph now runs through the TSE metrics decorator, so
    // stub IMetricsPublisher to keep the Redis-backed one out and avoid a live connection.
    private static ServiceProvider Build(VoiceSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureVoiceChannel(settings);
        services.AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfigureVoiceChannel_StreamingEnabled_ResolvesSegmentedOpenAiSpeechToText()
    {
        using var provider = Build(new VoiceSettings
        {
            Stt = new SttSettings { Streaming = new SegmentedSttConfig { Enabled = true } }
        });

        provider.GetRequiredService<ISpeechToText>().ShouldBeOfType<SegmentedSpeechToText>();
    }

    [Fact]
    public void ConfigureVoiceChannel_StreamingDisabled_ResolvesBareOpenAiSpeechToText()
    {
        using var provider = Build(new VoiceSettings());

        provider.GetRequiredService<ISpeechToText>().ShouldBeOfType<OpenAiSpeechToText>();
    }

    [Fact]
    public void ConfigureVoiceChannel_TrimEnabled_ResolvesSilenceTrimmedOpenAiTextToSpeech()
    {
        using var provider = Build(new VoiceSettings());

        provider.GetRequiredService<ITextToSpeech>().ShouldBeOfType<MeteredTextToSpeech>().Inner.ShouldBeOfType<SilenceTrimmingTextToSpeech>();
    }

    [Fact]
    public void ConfigureVoiceChannel_TrimDisabled_ResolvesBareOpenAiTextToSpeech()
    {
        using var provider = Build(new VoiceSettings
        {
            Tts = new TtsSettings { OpenAi = new OpenAiTtsConfig { TrailingSilenceTrimThreshold = 0 } }
        });

        provider.GetRequiredService<ITextToSpeech>().ShouldBeOfType<MeteredTextToSpeech>().Inner.ShouldBeOfType<OpenAiTextToSpeech>();
    }

    // Guards the gibberish-gate threshold wiring in ConfigureVoiceChannel. The two thresholds are
    // adjacent same-typed doubles, so a transposition would silently swap the avg_logprob floor
    // with the no_speech_prob ceiling — making the floor 0.7, which every real transcript
    // (avg_logprob <= 0) falls below, dropping ALL speech. Distinct non-default values prove the
    // configured settings (not just a coincidental default) flow through to the resolved dispatcher.
    [Fact]
    public async Task ConfigureVoiceChannel_ResolvedTranscriptDispatcher_WiresGateThresholdsInOrder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureVoiceChannel(new VoiceSettings
        {
            Stt = new SttSettings
            {
                OpenAi = new OpenAiSttConfig { AvgLogProbThreshold = -1.5, NoSpeechProbThreshold = 0.7 }
            }
        });
        services.AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>());
        services.AddSingleton<ITopicMinter>(new FakeTopicMinter());
        using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<TranscriptDispatcher>();
        var session = new SatelliteSession(
            "kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

        // Both signals inside the configured floor/ceiling -> dispatched. A transposition (floor
        // becomes 0.7) drops this, because avg_logprob -1.2 < 0.7.
        (await dispatcher.DispatchAsync(
            session, new TranscriptionResult { Text = "hola", AvgLogProb = -1.2, NoSpeechProb = 0.6 },
            "agent-1", null, null, null, default)).ShouldBeTrue();

        // Below the configured avg_logprob floor -> dropped (confirms the floor value flows).
        (await dispatcher.DispatchAsync(
            session, new TranscriptionResult { Text = "mumble", AvgLogProb = -1.8 },
            "agent-1", null, null, null, default)).ShouldBeFalse();

        // Above the configured no_speech_prob ceiling -> dropped (confirms the ceiling value flows).
        (await dispatcher.DispatchAsync(
            session, new TranscriptionResult { Text = "ffff", NoSpeechProb = 0.8 },
            "agent-1", null, null, null, default)).ShouldBeFalse();
    }

    // The arbiter is only reachable from the host through DI, so an unregistered ArbitrationSettings
    // or WakeArbiter fails at container build in production and nowhere else. A non-default WindowMs
    // proves the configured instance flows through rather than a coincidental `new ArbitrationSettings()`.
    [Fact]
    public void ConfigureVoiceChannel_ResolvesWakeArbiter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureVoiceChannel(new VoiceSettings
        {
            Arbitration = new ArbitrationSettings { WindowMs = 375 }
        });
        services.AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>());
        services.AddSingleton<ITopicMinter>(new FakeTopicMinter());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<WakeArbiter>().ShouldNotBeNull();
        provider.GetRequiredService<ArbitrationSettings>().WindowMs.ShouldBe(375);
    }

    // A conversation's topic is the agent's: this server keeps no topic store of its own and asks
    // the agent, at the address its settings give, for each one it opens.
    [Fact]
    public void ConfigureVoiceChannel_KeepsNoTopicStore_AndMintsTopicsThroughTheAgent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureVoiceChannel(new VoiceSettings { Agent = new AgentClientConfig { BaseUrl = "http://agent.test:8080" } });

        services.ShouldNotContain(d => d.ServiceType == typeof(IThreadStateStore) || d.ServiceType == typeof(IConversationFactory));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ITopicMinter>().ShouldBeOfType<AgentTopicsClient>();
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(AgentTopicsClient.ClientName)
            .BaseAddress.ShouldBe(new Uri("http://agent.test:8080/"));
    }
}