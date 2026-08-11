using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceSettingsBindingTests
{
    [Fact]
    public void VoiceSettings_BindsFromJson()
    {
        // Binds at the configuration root, matching the other channels (ChannelSettings).
        var json = """
        {
          "WyomingClient": { "TrailingSilenceMs": 800, "MaxUtteranceMs": 15000 },
          "Stt": {
            "OpenAi": { "BaseUrl": "http://lemonade:13305/v1", "Model": "Whisper-Medium", "Language": "es", "AvgLogProbThreshold": -1.2, "NoSpeechProbThreshold": 0.5 }
          },
          "Tts": {
            "OpenAi": { "Voice": "ef_dora", "Speed": 1.1 }
          },
          "Announce": {
            "Enabled": true,
            "Token": "secret",
            "BindToLoopbackOnly": false,
            "QueueMaxDepth": 8,
            "MaxTextLength": 500
          },
          "Satellites": {
            "kitchen-01": { "Identity": "household", "Room": "Kitchen", "WakeWord": "hey_jarvis", "Address": "tcp://host.docker.internal:10800", "Gate": { "SilenceRmsThreshold": 900, "MinSpeechMs": 400 } }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var settings = config.Get<VoiceSettings>();

        settings.ShouldNotBeNull();
        settings!.WyomingClient.TrailingSilenceMs.ShouldBe(800);
        settings.WyomingClient.MaxUtteranceMs.ShouldBe(15000);
        settings.Stt.OpenAi.BaseUrl.ShouldBe("http://lemonade:13305/v1");
        settings.Stt.OpenAi.Model.ShouldBe("Whisper-Medium");
        settings.Stt.OpenAi.Language.ShouldBe("es");
        settings.Stt.OpenAi.AvgLogProbThreshold.ShouldBe(-1.2);
        settings.Stt.OpenAi.NoSpeechProbThreshold.ShouldBe(0.5);
        settings.Tts.OpenAi.Voice.ShouldBe("ef_dora");
        settings.Tts.OpenAi.Speed.ShouldBe(1.1);
        settings.Tts.OpenAi.Model.ShouldBe("kokoro-v1");
        settings.Announce.Token.ShouldBe("secret");
        settings.Announce.MaxTextLength.ShouldBe(500);
        settings.Satellites.Count.ShouldBe(1);
        settings.Satellites["kitchen-01"].Identity.ShouldBe("household");
        settings.Satellites["kitchen-01"].Address.ShouldBe("tcp://host.docker.internal:10800");
        settings.Satellites["kitchen-01"].Gate.ShouldNotBeNull();
        settings.Satellites["kitchen-01"].Gate!.SilenceRmsThreshold.ShouldBe(900);
        settings.Satellites["kitchen-01"].Gate!.MinSpeechMs.ShouldBe(400);
    }

    [Fact]
    public void OpenAiSttConfig_DefaultThresholds_MatchShippedGibberishGate()
    {
        // These defaults are what production runs when appsettings/env omit the thresholds; a
        // sign flip here would silently invert the gate (see TranscriptDispatcher). avg_logprob
        // is a negative log scale (closer to 0 is better), no_speech_prob is 0..1 (higher = more
        // likely non-speech), so the floor is negative and the ceiling is in (0,1).
        var config = new OpenAiSttConfig();

        config.AvgLogProbThreshold.ShouldBe(-1.0);
        config.NoSpeechProbThreshold.ShouldBe(0.6);
    }

    [Fact]
    public void VoiceSettings_BindsGlobalAndPerSatelliteLocality()
    {
        var json = """
        {
          "Locality": "Madrid, Spain",
          "Satellites": {
            "kitchen-01": { "Identity": "household", "Room": "Kitchen" },
            "office-01": { "Identity": "household", "Room": "Office", "Locality": "Barcelona, Spain" }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var settings = config.Get<VoiceSettings>();

        settings.ShouldNotBeNull();
        settings!.Locality.ShouldBe("Madrid, Spain");
        settings.Satellites["kitchen-01"].Locality.ShouldBeNull();
        settings.Satellites["office-01"].Locality.ShouldBe("Barcelona, Spain");
    }

    [Fact]
    public void WithResolvedLocalityDefaults_SatelliteWithoutLocality_InheritsGlobal()
    {
        var settings = new VoiceSettings
        {
            Locality = "Madrid, Spain",
            Satellites = new() { ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen" } }
        };

        var resolved = settings.WithResolvedLocalityDefaults();

        resolved.Satellites["kitchen-01"].Locality.ShouldBe("Madrid, Spain");
    }

    [Fact]
    public void WithResolvedLocalityDefaults_SatelliteWithLocality_KeepsOwn()
    {
        var settings = new VoiceSettings
        {
            Locality = "Madrid, Spain",
            Satellites = new()
            {
                ["office-01"] = new() { Identity = "household", Room = "Office", Locality = "Barcelona, Spain" }
            }
        };

        var resolved = settings.WithResolvedLocalityDefaults();

        resolved.Satellites["office-01"].Locality.ShouldBe("Barcelona, Spain");
    }

    [Fact]
    public void WithResolvedLocalityDefaults_NoGlobalDefault_LeavesSatelliteNull()
    {
        var settings = new VoiceSettings
        {
            Satellites = new() { ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen" } }
        };

        var resolved = settings.WithResolvedLocalityDefaults();

        resolved.Satellites["kitchen-01"].Locality.ShouldBeNull();
    }

    [Fact]
    public void VoiceSettings_BindsPerSatelliteSttTtsOverridesFromEnvironmentVariables()
    {
        var vars = new Dictionary<string, string>
        {
            ["Satellites__kitchen-01__Identity"] = "household",
            ["Satellites__kitchen-01__Room"] = "Kitchen",
            ["Satellites__kitchen-01__Stt__OpenAi__Language"] = "en",
            ["Satellites__kitchen-01__Stt__OpenAi__AvgLogProbThreshold"] = "-0.5",
            ["Satellites__kitchen-01__Tts__OpenAi__Voice"] = "em_alex"
        };

        foreach (var (k, v) in vars)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var settings = config.Get<VoiceSettings>();

            var satellite = settings!.Satellites["kitchen-01"];
            satellite.Stt!.OpenAi!.Language.ShouldBe("en");
            satellite.Stt.OpenAi.AvgLogProbThreshold.ShouldBe(-0.5);
            satellite.Stt.OpenAi.NoSpeechProbThreshold.ShouldBeNull(); // unset stays null → global
            satellite.Tts!.OpenAi!.Voice.ShouldBe("em_alex");
        }
        finally
        {
            foreach (var k in vars.Keys)
            {
                Environment.SetEnvironmentVariable(k, null);
            }
        }
    }

    [Fact]
    public void TranscriptionOptionsFactory_Create_CarriesRoomAndLocality()
    {
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Fran's office",
            Locality = "Valladolid, Spain"
        };

        var options = TranscriptionOptionsFactory.Create("fran-office-01", config, null, default);

        options.Room.ShouldBe("Fran's office");
        options.Locality.ShouldBe("Valladolid, Spain");
    }

    // Symmetric with the Language override: the factory carries only the satellite's value and a
    // null falls back to the global Stt.OpenAi.Prompt inside the backend.
    [Fact]
    public void TranscriptionOptionsFactory_NoSatellitePrompt_LeavesTheTemplateNull()
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen" };

        TranscriptionOptionsFactory.Create("kitchen-01", config, null, default)
            .PromptTemplate.ShouldBeNull();
    }

    [Fact]
    public void TranscriptionOptionsFactory_SatellitePrompt_ReachesTheTemplate()
    {
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Stt = new SttOverrides { OpenAi = new OpenAiSttOverrides { Prompt = "room text" } }
        };

        TranscriptionOptionsFactory.Create("kitchen-01", config, null, default)
            .PromptTemplate.ShouldBe("room text");
    }
}