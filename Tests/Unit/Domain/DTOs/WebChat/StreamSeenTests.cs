using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

// A page resuming a stream is handed what the stream carried after the state it drew: the rest of each
// message it had begun, and every entry after the ones it had — never what it has twice.
public class StreamSeenTests
{
    private static readonly ChatStreamMessage _asked = new()
    {
        Content = "Cuéntame el cocido.", UserMessage = new UserMessageInfo("ana", null, "Ana")
    };

    [Fact]
    public void WhatAMessageGrewBy_SinceThePageDrewIt_IsHandedBack()
    {
        var seen = StreamSeen.Of([_asked, new ChatStreamMessage { Content = "Se llam", Reasoning = "pienso", MessageId = "m1" }]);

        var rest = seen.Since([_asked, new ChatStreamMessage { Content = "Se llamaron vuelcos", Reasoning = "pienso", MessageId = "m1" }]);

        rest.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Content.ShouldBe("aron vuelcos"),
            r => r.Reasoning.ShouldBeNull(),
            r => r.MessageId.ShouldBe("m1"));
    }

    [Fact]
    public void EntriesAfterTheOnesThePageHad_AreHandedBackWhole_AndNothingItHasIs()
    {
        var first = new ChatStreamMessage { Content = "Hecho.", MessageId = "m1" };
        var seen = StreamSeen.Of([_asked, first]);
        var carlos = new ChatStreamMessage
        {
            Content = "¿Y el postre?", UserMessage = new UserMessageInfo("carlos", null, "Carlos")
        };
        var second = new ChatStreamMessage { Content = "Flan.", MessageId = "m2" };

        var rest = seen.Since([_asked, first, carlos, second, new ChatStreamMessage { IsComplete = true, MessageId = "m2" }]);

        rest.Select(r => r.Content).ShouldBe(["¿Y el postre?", "Flan.", null]);
    }

    // The end of a message the page had begun comes after the page drew it.
    [Fact]
    public void TheEndOfAMessageThePageHadBegun_IsHandedBack()
    {
        var begun = new ChatStreamMessage { Content = "Hecho.", MessageId = "m1" };
        var seen = StreamSeen.Of([_asked, begun]);

        var rest = seen.Since([_asked, begun, new ChatStreamMessage { IsComplete = true, MessageId = "m1" }]);

        rest.ShouldHaveSingleItem().IsComplete.ShouldBeTrue();
    }
}