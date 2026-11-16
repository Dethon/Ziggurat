using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerScheduling.Services;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleFirePlannerTests
{
    [Fact]
    public void Plan_RecurringSchedule_AdvancesNextRunAndDoesNotDelete()
    {
        var s = new Schedule { Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc), now: DateTimeOffset.UtcNow);

        plan.DeleteAfterFire.ShouldBeFalse();
        plan.NextRunAt.ShouldBe(new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc));
        plan.Payload.AgentId.ShouldBe("jonas");
        plan.Payload.ReplyTo![0].ChannelId.ShouldBe("signalr");
        plan.Payload.Origin!.Kind.ShouldBe(MessageOriginKind.Schedule);
        plan.Payload.Origin!.ScheduleId.ShouldBe("n");
    }

    // The household the schedule is kept under rides the fire, so the agent refuses every fire of a
    // suspended household, whatever its delegation says; one kept under none names none.
    [Theory]
    [InlineData("fedcba9876543210fedcba9876543210", "fedcba9876543210fedcba9876543210")]
    [InlineData("", null)]
    public void Plan_CarriesTheSchedulesHousehold(string tenantId, string? expected)
    {
        var s = new Schedule { Id = "n", TenantId = tenantId, AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *" };

        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null, now: DateTimeOffset.UtcNow);

        plan.Payload.HouseholdId.ShouldBe(expected);
    }

    // The fire runs as whoever wrote the schedule, as their turn's channel vouched for them.
    [Fact]
    public void Plan_CarriesTheWritersPrincipal()
    {
        var s = new Schedule
        {
            Id = "n", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow,
            UserId = "ana", CreatedBy = new Principal("telegram", "FranTG")
        };

        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null, now: DateTimeOffset.UtcNow);

        plan.Payload.Principal.ShouldBe(new Principal("telegram", "FranTG"));
    }

    // The fired turn is told it is a schedule of its person's and that its reply is what reaches them:
    // a prompt saying "send it to her" left the model looking for a way to send, and four fires in
    // ten answered that nothing could reach the person while the reply itself was being delivered.
    [Fact]
    public void Plan_FramesThePrompt_AsAScheduleWhoseReplyIsTheDelivery()
    {
        var s = new Schedule
        {
            Id = "coffee-poem", AgentId = "jonas", Prompt = "Write Dana a two-line poem about coffee and send it to her.",
            RunAt = DateTime.UtcNow, UserId = "Dana Miller"
        };

        var payload = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null, now: DateTimeOffset.UtcNow)
            .Payload;

        // The prompt stays as its delegation signed it; what the turn is rides beside it as the
        // scheduler's report, framed after the delegation is checked.
        payload.Content.ShouldBe("Write Dana a two-line poem about coffee and send it to her.");
        payload.FireReport.ShouldNotBeNull();
        payload.FireReport.ShouldContain("scheduled task \"coffee-poem\"");
        payload.FireReport.ShouldContain("Dana Miller");
        payload.FireReport.ShouldContain("your reply is what is delivered");
    }

    [Fact]
    public void Plan_OneShotSchedule_DeletesAndUsesScheduleDeliverTo()
    {
        var s = new Schedule { Id = "once", AgentId = "jack", Prompt = "p", RunAt = DateTime.UtcNow, DeliverTo = ["telegram"], CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null, now: DateTimeOffset.UtcNow);

        plan.DeleteAfterFire.ShouldBeTrue();
        plan.NextRunAt.ShouldBeNull();
        plan.Payload.ReplyTo![0].ChannelId.ShouldBe("telegram");
    }

    [Fact]
    public void Plan_VoiceDeliverToWithSatelliteId_SplitsChannelAndAddress()
    {
        var s = new Schedule { Id = "s", AgentId = "mycroft", Prompt = "p", RunAt = DateTime.UtcNow, DeliverTo = ["voice:fran-office-01"], CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null, now: DateTimeOffset.UtcNow);

        var target = plan.Payload.ReplyTo!.ShouldHaveSingleItem();
        target.ChannelId.ShouldBe("voice");
        target.Address.ShouldBe("fran-office-01");
    }

    [Fact]
    public void Plan_BareVoiceDeliverTo_HasNullAddress()
    {
        var s = new Schedule { Id = "s", AgentId = "mycroft", Prompt = "p", RunAt = DateTime.UtcNow, DeliverTo = ["voice"], CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null, now: DateTimeOffset.UtcNow);

        var target = plan.Payload.ReplyTo!.ShouldHaveSingleItem();
        target.ChannelId.ShouldBe("voice");
        target.Address.ShouldBeNull();
    }

    [Fact]
    public void Plan_MultipleVoiceSatellites_MergesIntoSingleVoiceTarget()
    {
        var s = new Schedule
        {
            Id = "s", AgentId = "mycroft", Prompt = "p", RunAt = DateTime.UtcNow,
            DeliverTo = ["signalr", "voice:office-01", "voice:office-02"], CreatedAt = DateTime.UtcNow
        };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null, now: DateTimeOffset.UtcNow);

        plan.Payload.ReplyTo!.Count.ShouldBe(2);
        plan.Payload.ReplyTo![0].ChannelId.ShouldBe("signalr");
        var voice = plan.Payload.ReplyTo![1];
        voice.ChannelId.ShouldBe("voice");
        voice.Address.ShouldBe("office-01,office-02");
    }

    // The conversation id suffix and the payload's Timestamp must come from the same reading of
    // the clock — two separate DateTimeOffset.UtcNow calls can straddle a second boundary and,
    // more importantly, ignore the TimeProvider a test (or a scheduled agent's fixed one-clock
    // world) is asserting against.
    [Fact]
    public void Plan_TakesTheConversationIdSuffixAndTimestampFromTheSameClockReading()
    {
        var now = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        var s = new Schedule { Id = "once", AgentId = "jack", Prompt = "p", RunAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };

        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null, now: now);

        plan.Payload.ConversationId.ShouldBe($"sched-once-{now.ToUnixTimeMilliseconds()}");
        plan.Payload.Timestamp.ShouldBe(now);
    }
}