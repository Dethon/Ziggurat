using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.AgentSettings;

namespace Tests.Unit.WebChat.Client.State;

public class AgentSettingsStoreTests
{
    [Fact]
    public void SetAgentReasoningEffort_ExistingAgent_KeepsModel()
    {
        var dispatcher = new Dispatcher();
        var store = new AgentSettingsStore(dispatcher);
        dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        dispatcher.Dispatch(new SetAgentReasoningEffort("jack", "high"));

        store.State.ByAgent["jack"].ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "high"));
    }
}