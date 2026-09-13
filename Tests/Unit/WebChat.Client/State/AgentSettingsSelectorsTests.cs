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

    // A model the catalogue no longer lists is kept, not swapped for the default. Replacing it
    // silently changed which model answered without telling anyone, and the person's own pick is
    // the last thing that should be edited behind their back. The server decides what to do with
    // it: a Lemonade model it cannot serve fails the turn by name, a hosted one it does not offer
    // warns and answers on the agent's own. Either way the choice stays where the person left it.
    [Fact]
    public void Sanitize_ANonWhitelistedModel_IsKeptRatherThanSwappedForTheDefault()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("old/model", "low"), _jack);

        sanitized.ShouldBe(new AgentModelSettings("old/model", "low"));
    }

    // The outage case ADR 0042 is chiefly about. Discovery fails closed, so a box that goes down
    // empties every Lemonade id out of the catalogue. Dropping the pick here would send the next
    // turn with no patch at all — answered by a hosted provider, extracted from, and never
    // refused — which is the silent reroute the ADR exists to forbid.
    [Fact]
    public void Sanitize_ALemonadeModelWhoseHostWentDown_IsKeptSoTheServerCanRefuseIt()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("lemonade/local", "low"), _jack);

        sanitized.Model.ShouldBe("lemonade/local");
    }

    // The two halves joined: surviving Sanitize is only worth anything if the kept pick is still
    // put on the wire, which is what lets the server refuse it by name instead of answering.
    [Fact]
    public void AKeptLemonadeModel_IsStillSentAsAPatch()
    {
        var kept = AgentSettingsSelectors.Sanitize(new AgentModelSettings("lemonade/local", "low"), _jack);

        AgentSettingsSelectors.GetConfigPatch(StateWith(kept), [_jack], "jack")
            .ShouldBe(new AgentConfigPatch { Model = "lemonade/local" });
    }

    // Effort keeps its fallback while the model no longer has one. An effort is a small set of
    // fixed words the server warns and continues on, so a stale one costs nothing and showing it
    // as selected when it is not honoured would be the lie. A model names what answers.
    [Fact]
    public void Sanitize_UnknownEffort_FallsBackToDefault()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("z-ai/glm-5.2", "turbo"), _jack);

        sanitized.ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "low"));
    }

    // A Lemonade id is an override like any other, by id, and survives the catalogue listing it
    // or not — the vanished case is its own test above, because it is the one the outage hits.
    [Fact]
    public void Sanitize_ALemonadeModel_IsValidWhileTheCatalogueListsIt()
    {
        var withLemonade = _jack with
        {
            PatchableModels = [.. _jack.PatchableModels!, new PatchableModel("lemonade/local", "local")]
        };

        AgentSettingsSelectors.Sanitize(new AgentModelSettings("lemonade/local", "low"), withLemonade)
            .Model.ShouldBe("lemonade/local");
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