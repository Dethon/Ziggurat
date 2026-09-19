using Infrastructure.Judgments;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.McpChannelVoice;

public class ApprovalSettingsBindingTests
{
    [Fact]
    public void Bind_OverridesFromJson()
    {
        var json = """
        {
          "Approval": {
            "Judgment": {
              "Enabled": false,
              "DeadlineMs": 700,
              "Sure": 0.85,
              "Counter": 0.15,
              "Lean": 0.6
            }
          },
          "OpenRouter": { "ApiKey": "k" },
          "TypeSafe": { "Model": "typesafe/jev-9.9" }
        }
        """;

        var settings = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build()
            .Get<VoiceSettings>()!;

        settings.Approval.Judgment.ShouldBe(new ApprovalJudgmentSettings
        {
            Enabled = false,
            DeadlineMs = 700,
            Sure = 0.85,
            Counter = 0.15,
            Lean = 0.6
        });
        settings.OpenRouter.ApiKey.ShouldBe("k");
        settings.TypeSafe.Model.ShouldBe("typesafe/jev-9.9");
    }

    // Every bar the spec names ships in the voice server's own appsettings, readable from
    // production, beside the judge that answers them.
    [Fact]
    public void Bind_ShippedAppSettings_CarriesTheJudgeAndEveryBar()
    {
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(McpServerRegistrations.RepoRoot, "McpChannelVoice/appsettings.json"))
            .Build()
            .Get<VoiceSettings>()!;

        settings.OpenRouter.ApiKey.ShouldBeEmpty();
        settings.TypeSafe.ApiUrl.ShouldBe("https://openrouter.ai/api/");
        settings.TypeSafe.Model.ShouldBe("typesafe/jev-1.13-20260917");
        settings.Approval.Judgment.ShouldBe(new ApprovalJudgmentSettings
        {
            Enabled = true,
            DeadlineMs = 1000,
            Sure = 0.9,
            Counter = 0.1,
            Lean = 0.5
        });
        settings.Approval.Judgment.ShouldBe(new ApprovalJudgmentSettings());
    }
}