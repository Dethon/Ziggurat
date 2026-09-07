using Domain.Channels;
using Shouldly;

namespace Tests.Unit.Domain.Channels;

// The conversation id of an unprompted fire is what keeps two fires apart. A schedule fires at
// most once a minute, but a watch on a flapping sensor can fire twice inside one second, and two
// fires sharing an id would be two prompts in one conversation.
public class FirePlanningTests
{
    [Fact]
    public void ConversationId_TwoFiresInTheSameSecond_AreTwoConversations()
    {
        var first = new DateTimeOffset(2026, 9, 5, 10, 0, 0, 100, TimeSpan.Zero);
        var second = first.AddMilliseconds(400);

        FirePlanning.ConversationId("watch", "door-flaps", first)
            .ShouldNotBe(FirePlanning.ConversationId("watch", "door-flaps", second));
    }

    [Fact]
    public void ConversationId_IsThePrefixTheIdAndTheInstant()
    {
        var at = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

        FirePlanning.ConversationId("sched", "morning-news", at).ShouldBe($"sched-morning-news-{at.ToUnixTimeMilliseconds()}");
    }
}