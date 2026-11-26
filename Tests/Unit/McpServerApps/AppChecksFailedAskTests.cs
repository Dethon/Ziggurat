using McpServerApps.Checks;
using Shouldly;

namespace Tests.Unit.McpServerApps;

// What a check makes of a /_ask its preview's page was answered 400 or worse: the tools' envelope is a failed
// ask, which the page may well have handled; anything else is a failed request (null here).
public class AppChecksFailedAskTests
{
    private const string Asked = """{"text":"carbs"}""";

    [Fact]
    public void AnEnvelope_IsAFailedAsk_WithWhatItSaid()
    {
        var failed = AppChecks.ReadFailedAsk(422, Asked,
            """{"ok":false,"errorCode":"invalid_reply","message":"No JSON.","hint":"Ask again.","reply":"Twelve grams."}""");

        failed.ShouldBe(new FailedAsk(422, "carbs", "invalid_reply", "No JSON.", "Ask again.", "Twelve grams.", null));
    }

    // The tools' taxonomy reads an envelope naming no code as internal_error, and so does a check: still the
    // ask failing, never a failed request.
    [Fact]
    public void AnEnvelopeNamingNoCode_IsAFailedAsk_CodedInternalError()
    {
        var failed = AppChecks.ReadFailedAsk(503, Asked, """{"ok":false,"message":"Nobody answered."}""");

        failed.ShouldNotBeNull();
        failed.ErrorCode.ShouldBe("internal_error");
        failed.Message.ShouldBe("Nobody answered.");
        failed.Text.ShouldBe("carbs");
    }

    [Theory]
    [InlineData("<!doctype html><title>Not found</title>")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":false,"errorCode":7,"message":"x"}""")]
    public void AnAnswerThatIsNoEnvelope_IsNoFailedAsk(string answer) =>
        AppChecks.ReadFailedAsk(404, Asked, answer).ShouldBeNull();
}