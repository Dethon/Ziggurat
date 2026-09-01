using Domain.DTOs.Channel;
using Shouldly;
using WebChat.Client.State.AgentSettings;

namespace Tests.Unit.WebChat.Client.State;

public class AgentSettingsSelectorsTests
{
    private static readonly AgentCatalogEntry _jack = new(
        "jack", "Jack", null,
        "openai/gpt-5.6-luna", "low",
        [new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"), new PatchableModel("z-ai/glm-5.2", "GLM 5.2")],
        AgentConfigPatch.SupportedEfforts);

    private static AgentSettingsState StateWith(AgentModelSettings settings) =>
        new() { ByAgent = new Dictionary<string, AgentModelSettings> { ["jack"] = settings } };

    [Fact]
    public void GetConfigPatch_AllValuesMatchDefaults_ReturnsNull()
    {
        var state = StateWith(new AgentModelSettings("openai/gpt-5.6-luna", "low"));

        AgentSettingsSelectors.GetConfigPatch(state, [_jack], "jack").ShouldBeNull();
    }

    [Fact]
    public void GetConfigPatch_ModelDiffers_ReturnsModelOnlyPatch()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "low"));

        AgentSettingsSelectors.GetConfigPatch(state, [_jack], "jack")
            .ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
    }

    [Fact]
    public void GetConfigPatch_BothDiffer_ReturnsBothFields()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "max"));

        AgentSettingsSelectors.GetConfigPatch(state, [_jack], "jack")
            .ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "max" });
    }

    [Fact]
    public void GetConfigPatch_UnknownAgent_ReturnsNull()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "max"));

        AgentSettingsSelectors.GetConfigPatch(state, [_jack], "ghost").ShouldBeNull();
    }

    [Fact]
    public void Sanitize_NonWhitelistedModel_FallsBackToDefault()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("old/model", "low"), _jack);

        sanitized.ShouldBe(new AgentModelSettings("openai/gpt-5.6-luna", "low"));
    }

    [Fact]
    public void Sanitize_UnknownEffort_FallsBackToDefault()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("z-ai/glm-5.2", "turbo"), _jack);

        sanitized.ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "low"));
    }

    // A Lemonade id is a valid override exactly while the catalogue lists it, by id like any
    // other; one that vanished from the host is dropped to the default like any other.
    [Fact]
    public void Sanitize_ALemonadeModel_IsValidWhileTheCatalogueListsIt()
    {
        var withLemonade = _jack with
        {
            PatchableModels = [.. _jack.PatchableModels!, new PatchableModel("lemonade/local", "local")]
        };

        AgentSettingsSelectors.Sanitize(new AgentModelSettings("lemonade/local", "low"), withLemonade)
            .Model.ShouldBe("lemonade/local");
        AgentSettingsSelectors.Sanitize(new AgentModelSettings("lemonade/local", "low"), _jack)
            .Model.ShouldBe("openai/gpt-5.6-luna");
    }

    // The lemon is a function of the id alone: the catalogue carries no provider field, and the
    // namespace a Lemonade model carries everywhere inside the system is what marks it here too.
    [Theory]
    [InlineData("lemonade/Qwen3.8-27B-GGUF-UD-Q4_K_XL", true)]
    [InlineData("Lemonade/gemma", true)]
    [InlineData("openai/gpt-5.6-luna", false)]
    [InlineData("z-ai/glm-5.2", false)]
    [InlineData("lemonade", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLemonadeModel_IsDecidedByThePrefixAlone(string? modelId, bool expected)
    {
        AgentSettingsSelectors.IsLemonadeModel(modelId).ShouldBe(expected);
    }

    // What the gear button and the listbox show for a model: its catalogue name when it has one,
    // the id itself when the catalogue no longer lists it, nothing for no override.
    [Fact]
    public void ModelNameFor_ReadsTheCatalogueNameAndFallsBackToTheId()
    {
        AgentSettingsSelectors.ModelNameFor(_jack, "z-ai/glm-5.2").ShouldBe("GLM 5.2");
        AgentSettingsSelectors.ModelNameFor(_jack, "lemonade/gone").ShouldBe("lemonade/gone");
        AgentSettingsSelectors.ModelNameFor(_jack, null).ShouldBeNull();
    }
}