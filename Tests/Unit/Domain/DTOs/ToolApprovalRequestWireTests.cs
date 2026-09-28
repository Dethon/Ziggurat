using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.DTOs;

// The codes cross the agent-to-channel hop as part of the approval request's JSON, and a channel
// older than the screen receives a field it ignores.
public class ToolApprovalRequestWireTests
{
    [Fact]
    public void TheScreen_SurvivesTheChannelHop()
    {
        var sent = new RequestApprovalParams
        {
            ConversationId = "7:42",
            Mode = ApprovalMode.Request,
            Requests = [new ToolApprovalRequest(null, "domain__filesystem__exec", new Dictionary<string, object?>()) { Screen = [ExecScreenCodes.SendsOut] }]
        };

        var arguments = ChannelProtocol.ToArguments(sent);
        var received = ChannelProtocol.Deserialize<IReadOnlyList<ToolApprovalRequest>>((System.Text.Json.JsonElement)arguments["requests"]!)!;

        received.ShouldHaveSingleItem().Screen.ShouldBe([ExecScreenCodes.SendsOut]);
    }

    [Fact]
    public void ARequestWithNoScreen_ReadsAsNone()
    {
        var received = ChannelProtocol.Deserialize<ToolApprovalRequest>(
            System.Text.Json.JsonDocument.Parse("""{"messageId":null,"toolName":"t","arguments":{}}""").RootElement)!;

        received.Screen.ShouldBeNull();
    }
}