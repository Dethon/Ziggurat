namespace Domain.Prompts;

// What speaking rather than writing does to an answer (Sections/voice.md). Its last paragraph is
// the override written out in prose; the document's header says the same thing in a form the
// assembly can check.
public static class VoicePrompt
{
    public const string Name = "voice";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string Instructions = _document.Text;

    public static readonly PromptClaim OneSentenceTwelveWords =
        _document.Claim("voice.one-sentence-twelve-words");

    public static readonly PromptClaim SeveralSentencesOnlyWhenAsked =
        _document.Claim("voice.several-sentences-only-when-asked");

    public static readonly PromptClaim ActionConfirmedInTwoWords =
        _document.Claim("voice.action-confirmed-in-two-words");

    public static readonly PromptClaim ValueSaidIsRepeated =
        _document.Claim("voice.value-said-is-repeated");

    public static readonly PromptClaim FactIsTheValueAlone =
        _document.Claim("voice.fact-is-the-value-alone");

    public static readonly PromptClaim FailureIsOneClause =
        _document.Claim("voice.failure-is-one-clause");

    public static readonly PromptClaim UnclearRequestIsActedOn =
        _document.Claim("voice.unclear-request-is-acted-on");

    public static readonly PromptClaim NothingIsNarrated =
        _document.Claim("voice.nothing-is-narrated");

    public static readonly PromptClaim NothingUnspeakable =
        _document.Claim("voice.nothing-unspeakable");

    public static readonly PromptClaim AbbreviationsAreSpelledOut =
        _document.Claim("voice.abbreviations-are-spelled-out");

    public static readonly PromptClaim OneWordBeforeSlowWork =
        _document.Claim("voice.one-word-before-slow-work");
}