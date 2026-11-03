using System.Text.Json;
using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

// A watch's fire is an agent-initiated message like a schedule's, and nothing else about it is a
// schedule: it has no schedule id, it is not counted as a schedule execution, and it names the
// watch it came from.
public class MessageOriginTests
{
    [Fact]
    public void AWatchOrigin_RoundTripsTheWatchIdAndTitleOverTheWire()
    {
        var payload = new ChannelMessageNotification
        {
            ConversationId = "watch-laura-sugar-high-1",
            Sender = "watch",
            Content = "Laura's sugar fired",
            AgentId = "jonas",
            UserId = "fran",
            Origin = new MessageOrigin(MessageOriginKind.Watch, null, WatchId: "laura-sugar-high", Title: "Laura's sugar"),
            Timestamp = DateTimeOffset.UtcNow
        };

        var element = JsonSerializer.SerializeToElement(payload, ChannelProtocol.SerializerOptions);
        var restored = ChannelProtocol.Deserialize<ChannelMessageNotification>(element).ShouldNotBeNull();

        restored.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Watch, null, "laura-sugar-high", "Laura's sugar"));
        restored.UserId.ShouldBe("fran");
        element.GetProperty("origin").GetProperty("kind").GetString().ShouldBe("Watch");
    }

    [Fact]
    public void AScheduleOrigin_StillReadsWithoutTheNewFields()
    {
        var element = JsonSerializer.SerializeToElement(
            new { kind = "Schedule", scheduleId = "morning-news" }, ChannelProtocol.SerializerOptions);

        var restored = ChannelProtocol.Deserialize<MessageOrigin>(element).ShouldNotBeNull();

        restored.ShouldBe(new MessageOrigin(MessageOriginKind.Schedule, "morning-news"));
    }

    [Fact]
    public void AWatchFire_IsNotAScheduleExecution()
    {
        var message = new ChannelMessage
        {
            ConversationId = "c", Content = "look into it", Sender = "watch", ChannelId = "homeassistant",
            AgentId = "jonas", Origin = new MessageOrigin(MessageOriginKind.Watch, null, WatchId: "laura-sugar-high")
        };

        ScheduleExecutionEvent.FromMessage(message, durationMs: 1, success: true, error: null).ShouldBeNull();
    }

    // A schedule's fire reaches its turn framed with the scheduler's report, and the dashboard's run
    // list showed that frame ("<fire-report note=...") where the schedule's own prompt belongs.
    [Fact]
    public void AScheduleExecution_RecordsTheSchedulesPrompt_NotTheReportFramedBesideIt()
    {
        var message = new ChannelMessage
        {
            ConversationId = "c", Sender = "scheduler", ChannelId = "signalr", AgentId = "jonas",
            Content = FireReports.Frame("Tell Tomás to water the plants.", "This turn is the scheduled task \"plants\"."),
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "plants")
        };

        ScheduleExecutionEvent.FromMessage(message, durationMs: 1, success: true, error: null)
            .ShouldNotBeNull().Prompt.ShouldBe("Tell Tomás to water the plants.");
    }
}