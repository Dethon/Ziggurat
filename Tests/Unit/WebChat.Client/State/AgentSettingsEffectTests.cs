using System.Text.Json;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentSettingsEffectTests : IDisposable
{
    private static readonly AgentCatalogEntry _jack = new(
        "jack", "Jack", null,
        "openai/gpt-5.6-luna", "low",
        [new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"), new PatchableModel("z-ai/glm-5.2", "GLM 5.2")],
        AgentConfigPatch.SupportedEfforts);

    // The same agent after the server narrowed what it will accept.
    private static readonly AgentCatalogEntry _jackNarrowed = new(
        "jack", "Jack", null,
        "openai/gpt-5.6-luna", "low",
        [new PatchableModel("openai/gpt-5.6-luna", "GPT Luna")],
        AgentConfigPatch.SupportedEfforts);

    private static readonly AgentCatalogEntry _nabu = new(
        "nabu", "Nabu", null,
        "openai/gpt-5.6-luna", "low",
        [new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"), new PatchableModel("z-ai/glm-5.2", "GLM 5.2")],
        AgentConfigPatch.SupportedEfforts);

    private readonly FakeLocalStorageService _storage = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly AgentSettingsStore _store;
    private readonly AgentSettingsEffect _effect;

    public AgentSettingsEffectTests()
    {
        _store = new AgentSettingsStore(_dispatcher);
        _effect = new AgentSettingsEffect(
            _store, _dispatcher, _storage, NullLogger<AgentSettingsEffect>.Instance);
    }

    public void Dispose()
    {
        _effect.Dispose();
        _store.Dispose();
    }

    [Fact]
    public void SetAgents_StoredSettings_SanitizesAndLoadsThem()
    {
        _storage.Seed("agentConfigPatch:jack", """{"Model":"z-ai/glm-5.2","ReasoningEffort":"turbo"}""");

        _dispatcher.Dispatch(new SetAgents([_jack]));

        _store.State.ByAgent["jack"].ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "low"));
    }

    [Fact]
    public void SetAgents_NothingStored_LoadsTheAgentDefaults()
    {
        _dispatcher.Dispatch(new SetAgents([_jack]));

        _store.State.ByAgent["jack"].ShouldBe(new AgentModelSettings("openai/gpt-5.6-luna", "low"));
    }

    // The live catalog is the only place a narrowing shows up. Leaving the old selection in
    // place means every turn sends a model the server rejects, while the menu shows it as the
    // current one.
    [Fact]
    public void SetAgents_LiveCatalogNarrowsTheModels_ResanitizesAKnownAgent()
    {
        _dispatcher.Dispatch(new SetAgents([_jack]));
        _dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        _dispatcher.Dispatch(new SetAgents([_jackNarrowed]));

        _store.State.ByAgent["jack"].Model.ShouldBe("openai/gpt-5.6-luna");
    }

    [Fact]
    public void SetAgents_LiveCatalogAddsAnAgent_LoadsItsPersistedSettings()
    {
        _storage.Seed("agentConfigPatch:nabu", """{"Model":"z-ai/glm-5.2","ReasoningEffort":"high"}""");
        _dispatcher.Dispatch(new SetAgents([_jack]));

        _dispatcher.Dispatch(new SetAgents([_jack, _nabu]));

        _store.State.ByAgent["nabu"].ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "high"));
    }

    // A catalog that still offers what the user picked must not undo the pick.
    [Fact]
    public void SetAgents_LiveCatalogStillOffersTheModel_KeepsTheSelection()
    {
        _dispatcher.Dispatch(new SetAgents([_jack]));
        _dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        _dispatcher.Dispatch(new SetAgents([_jack]));

        _store.State.ByAgent["jack"].Model.ShouldBe("z-ai/glm-5.2");
    }

    [Fact]
    public async Task StateChange_ChangedEntry_PersistsToStorage()
    {
        _dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        // The write is fire-and-forget, so the key appears when the effect gets to it rather than
        // within any particular span.
        await TestChat.Eventually(() => _storage.Values.ContainsKey("agentConfigPatch:jack"));

        _storage.Values["agentConfigPatch:jack"]
            .ShouldBe(JsonSerializer.Serialize(new AgentModelSettings("z-ai/glm-5.2", null)));
    }
}