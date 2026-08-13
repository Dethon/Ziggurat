using Domain.Agents;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

public class AgentKeyTests
{
    [Fact]
    public void ChatConversationParts_InvertsWhatToStringRendered()
    {
        var rendered = new AgentKey("560:7", "jackbot").ToString();

        AgentKey.ChatConversationParts(rendered).ShouldBe(("jackbot", 560L, 7L));
    }

    // An agent id may itself carry ':', so the numeric tail is read from the end.
    [Fact]
    public void ChatConversationParts_LeavesAColonInsideTheAgentIdAlone()
    {
        AgentKey.ChatConversationParts("agent-key:team:jackbot:560:7")
            .ShouldBe(("team:jackbot", 560L, 7L));
    }

    // The GUID a session falls back to when it has no key yet belongs to no conversation.
    [Fact]
    public void ChatConversationParts_YieldsNothingForAKeyNothingRendered()
    {
        AgentKey.ChatConversationParts(Guid.Empty.ToString()).ShouldBeNull();
        AgentKey.ChatConversationParts("agent-key:jackbot:not:numeric").ShouldBeNull();
    }
}