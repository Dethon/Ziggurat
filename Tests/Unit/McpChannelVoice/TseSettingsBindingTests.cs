using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TseSettingsBindingTests
{
    [Fact]
    public void Bind_Defaults_WhenSectionMissing()
    {
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentId"] = "mycroft" })
            .Build()
            .Get<VoiceSettings>()!;

        settings.Tse.Mode.ShouldBe(TseMode.Off);
        settings.Tse.Endpoint.ShouldBe("http://tse-extractor:9098");
        settings.Tse.TimeoutMs.ShouldBe(90000);
        settings.Tse.NoiseFloorThreshold.ShouldBe(400);
        settings.Tse.AuditDir.ShouldBeNull();
        settings.Tse.AuditMaxPairs.ShouldBe(50);
    }

    [Fact]
    public void Bind_OverridesFromJson()
    {
        var json = """
        {
          "Tse": {
            "Mode": "Auto",
            "Endpoint": "http://tse-extractor:1234",
            "TimeoutMs": 5000,
            "NoiseFloorThreshold": 250,
            "AuditDir": "/tse-audit",
            "AuditMaxPairs": 10
          }
        }
        """;

        var settings = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build()
            .Get<VoiceSettings>()!;

        settings.Tse.Mode.ShouldBe(TseMode.Auto);
        settings.Tse.Endpoint.ShouldBe("http://tse-extractor:1234");
        settings.Tse.TimeoutMs.ShouldBe(5000);
        settings.Tse.NoiseFloorThreshold.ShouldBe(250);
        settings.Tse.AuditDir.ShouldBe("/tse-audit");
        settings.Tse.AuditMaxPairs.ShouldBe(10);
    }
}