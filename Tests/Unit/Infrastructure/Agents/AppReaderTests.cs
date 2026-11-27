using Domain.DTOs.Channel;
using Domain.Monitor;
using Domain.Tools;
using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// What a built app's read is told when the household will not have the agent act for its viewer: a
// refusal that holds until somebody changes it is never told as worth asking again, and only a standing
// that could not be read is.
public class AppReaderTests
{
    private static readonly Account _viewer = new("house", "her");

    public static TheoryData<StandingRefusal, string> Refusals => new()
    {
        { new StandingRefusal.Suspended("house"), ToolError.Codes.PermissionDenied },
        { new StandingRefusal.Disabled(_viewer), ToolError.Codes.PermissionDenied },
        { new StandingRefusal.SugaryOff("house"), ToolError.Codes.PermissionDenied },
        { new StandingRefusal.Unchecked("house"), ToolError.Codes.TransientDependency }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AStandingRefusal_IsAnsweredWithItsOwnCode(StandingRefusal refusal, string code)
    {
        var error = AppReader.Refused(refusal);

        error.ErrorCode.ShouldBe(code, error.Message);
        error.Hint.ShouldNotBeNullOrWhiteSpace();
    }
}