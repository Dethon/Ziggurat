using Shouldly;

namespace Tests.Eval.Harness;

// The checks that read what the agent said, proven against fixed strings with no model involved.
// Every one of them exists because a spoken reply is read out by a text-to-speech engine: a
// markdown asterisk, an entity id or a url is either pronounced or silently swallowed, and both
// are worse than the sentence the agent meant to say.
public class ReplyChecksTests
{
    [Fact]
    public void AReplyWithinItsLimits_Passes()
    {
        Failures(new ReplyExpectation { MaxSentences = 1, MaxWords = 8 }, "Listo, ocho minutos.")
            .ShouldBeEmpty();
    }

    [Fact]
    public void AReplyOverTheSentenceLimit_FailsAndQuotesIt()
    {
        var failures = Failures(
            new ReplyExpectation { MaxSentences = 1 },
            // No one-word opener: the contract allows one of those before slow work, so a reply
            // starting with one is counted from the second sentence and this test would be about
            // that rule instead of about the limit.
            "He puesto el temporizador. Suena a las ocho y cinco. Te aviso cuando suene.");

        var failure = failures.ShouldHaveSingleItem();
        failure.ShouldContain("3 sentences");
        failure.ShouldContain("Te aviso cuando suene.");
    }

    [Fact]
    public void AQuestionCountsAsASentence_InEitherLanguagesPunctuation()
    {
        // Spanish opens a question with '¿' and closes it with '?', and the reply this suite most
        // wants to bound — "which room?" — is a question in both.
        Failures(new ReplyExpectation { MaxSentences = 1 }, "¿En qué habitación? Dime cuál.")
            .ShouldHaveSingleItem().ShouldContain("2 sentences");
    }

    [Fact]
    public void AReplyOverTheWordLimit_Fails()
    {
        Failures(new ReplyExpectation { MaxWords = 3 }, "Listo, ocho minutos para la pasta")
            .ShouldHaveSingleItem().ShouldContain("6 words");
    }

    [Theory]
    [InlineData("Listo, **ocho** minutos.", "markdown")]
    [InlineData("- Listo\n- Ocho minutos", "markdown")]
    [InlineData("Listo `ocho` minutos.", "markdown")]
    [InlineData("Listo, ocho minutos 🍝", "emoji")]
    [InlineData("Lo he escrito en /timers/pasta/timer.json", "a file path")]
    [InlineData("He apagado light.kitchen", "an entity id")]
    [InlineData("Míralo en https://home.local/timers", "a url")]
    public void ASpokenReplyCarryingSomethingUnspeakable_Fails(string reply, string named)
    {
        Failures(new ReplyExpectation { Spoken = true }, reply)
            .ShouldHaveSingleItem().ShouldContain(named);
    }

    [Fact]
    public void APlainSpokenSentence_CarriesNothingUnspeakable()
    {
        Failures(new ReplyExpectation { Spoken = true }, "Listo, te aviso en ocho minutos.")
            .ShouldBeEmpty();
    }

    [Fact]
    public void ADeclaredValueMissingFromTheReply_Fails()
    {
        Failures(new ReplyExpectation { Mentions = [new SpokenValue("the remaining time", "5", "cinco")] },
                "Listo, ya está.")
            .ShouldHaveSingleItem().ShouldContain("the remaining time");
    }

    [Theory]
    [InlineData("Quedan 5 minutos.")]
    [InlineData("Quedan cinco minutos.")]
    public void AnyDeclaredSpellingOfAValue_Passes(string reply)
    {
        // The reply's language and its own choice between a numeral and a word are the model's;
        // what the scenario asserts is that the value the user said survived into the answer.
        Failures(new ReplyExpectation { Mentions = [new SpokenValue("the remaining time", "5", "cinco")] }, reply)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Quedan 15 minutos.")]
    [InlineData("Quedan veinticinco minutos.")]
    public void AValueSittingInsideAnother_DoesNotCountAsCarryingIt(string reply)
    {
        // The failure this check exists to catch is the wrong number, and "5" is inside "15".
        Failures(new ReplyExpectation { Mentions = [new SpokenValue("the remaining time", "5", "cinco")] }, reply)
            .ShouldHaveSingleItem().ShouldContain("the remaining time");
    }

    [Fact]
    public void AReplyNarratingWhatShouldStayInternal_Fails()
    {
        // Half of what the voice contract asks for is silence about mechanism: the delete and
        // recreate behind "two more minutes" is not something a person asked to hear about.
        Failures(new ReplyExpectation { NeverSays = ["borrado", "eliminad"] },
                "He eliminado el temporizador y he creado otro de diez minutos.")
            .ShouldHaveSingleItem().ShouldContain("eliminad");
    }

    [Fact]
    public void AnAcknowledgementBeforeTheAnswer_DoesNotCountAgainstAOneSentenceLimit()
    {
        // The voice section allows one word before slow work and one sentence of answer. A limit
        // that counted the acknowledgement would fail the model for obeying the rule beside it.
        var failures = Failures(
            new ReplyExpectation { MaxSentences = 1, MaxWords = 12 },
            "Consultando.\nQuedan cinco minutos.");

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void TwoSentencesOfAnswer_StillFailAOneSentenceLimit()
    {
        var failures = Failures(
            new ReplyExpectation { MaxSentences = 1 },
            "Quedan cinco minutos. Suena a las ocho y cinco.");

        failures.ShouldHaveSingleItem().ShouldContain("2 sentences");
    }

    private static IReadOnlyList<string> Failures(ReplyExpectation expectation, string reply) =>
        ReplyChecks.Failures(expectation, reply);
}