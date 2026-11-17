using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

// What a channel answers send_reply with, read by the agent: the turn keys a stop handed back, so a turn
// waiting behind the one that just ended is never run. Optional both ways: a channel that answers "ok",
// or anything else it says, names nothing, and a field a later channel adds is ignored.
public class ReplyReceiptTests
{
    [Fact]
    public void AReceiptNamingTurnKeys_ReadsBackAsThoseKeys() =>
        ReplyReceipt.Parse(new ReplyReceipt(["steer-1", "queued-2"]).ToWire()).ReturnedTurnKeys.ShouldBe(["steer-1", "queued-2"]);

    [Theory]
    [InlineData("ok")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[1,2]")]
    [InlineData("{\"returnedTurnKeys\":7}")]
    public void AnythingElseAChannelAnswers_NamesNothing(string? answer) =>
        ReplyReceipt.Parse(answer).ReturnedTurnKeys.ShouldBeEmpty();

    [Fact]
    public void AFieldItDoesNotKnow_IsIgnored() =>
        ReplyReceipt.Parse("{\"returnedTurnKeys\":[\"steer-1\"],\"later\":true}").ReturnedTurnKeys.ShouldBe(["steer-1"]);

    // Nothing handed back is the plain answer every channel gave before, so an agent from before reads it
    // as it always did.
    [Fact]
    public void ANoneReceipt_GoesOutAsTheOkEveryChannelAnswered() =>
        ReplyReceipt.None.ToWire().ShouldBe("ok");
}