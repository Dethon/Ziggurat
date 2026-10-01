using Domain.Tools.Scheduling.Vfs;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class SchedulePathTests
{
    [Theory]
    [InlineData("/", ScheduleNodeKind.Root)]
    [InlineData("/jonas", ScheduleNodeKind.AgentDir)]
    [InlineData("/jonas/agent_info.json", ScheduleNodeKind.AgentInfoFile)]
    [InlineData("/jonas/morning-news", ScheduleNodeKind.ScheduleDir)]
    [InlineData("/jonas/morning-news/schedule.json", ScheduleNodeKind.ScheduleFile)]
    [InlineData("/jonas/morning-news/status.json", ScheduleNodeKind.StatusFile)]
    [InlineData("/jonas/morning-news/run_now", ScheduleNodeKind.RunNowFile)]
    [InlineData("/jonas/morning-news/run_now.sh", ScheduleNodeKind.Unknown)]
    [InlineData("/jonas/morning-news/bogus", ScheduleNodeKind.Unknown)]
    public void Parse_ResolvesNodeKinds(string path, ScheduleNodeKind expected)
    {
        var node = SchedulePath.Parse(path);
        node.Kind.ShouldBe(expected);
    }

    [Fact]
    public void Parse_CapturesAgentAndScheduleSegments()
    {
        var node = SchedulePath.Parse("/jonas/morning-news/schedule.json");
        node.AgentId.ShouldBe("jonas");
        node.ScheduleId.ShouldBe("morning-news");
    }

    [Theory]
    [InlineData("/status.json")]                       // reserved name as agent id
    [InlineData("/jonas/status.json")]                 // reserved name as schedule id
    [InlineData("/jonas/schedule.json")]               // reserved name as schedule id
    [InlineData("/jonas/run_now")]                     // reserved name as schedule id
    [InlineData("/jonas/..")]                          // traversal marker as id
    [InlineData("/../morning-news/schedule.json")]     // traversal marker as agent id
    [InlineData("/jonas/./schedule.json")]             // dot segment as schedule id
    public void Parse_ReservedNamesOrDotSegments_AreUnknown(string path) =>
        SchedulePath.Parse(path).Kind.ShouldBe(ScheduleNodeKind.Unknown);
}