namespace Domain.Prompts;

// What speaking rather than writing does to an answer. It lived as a three-kilobyte string inside
// `agents[nabu].customInstructions`, where it was unreviewable in a diff, unreachable from a test
// and impossible to state a budget or a conflict for. The words are unchanged from that string.
//
// Its last paragraph is the override written out in prose, for the model. The declaration in
// `PromptManifest` is the same statement in a form the assembly can check, and the two must keep
// saying the same thing: this section is read after the sections whose formatting and verbosity
// guidance it beats, and before the language rule it does not touch.
public static class VoicePrompt
{
    public const string Name = "voice";

    public const string Instructions =
        """
        VOICE RULES. Everything you say is spoken aloud by text to speech. The examples below show the shape of a good answer, not words you have to say.

        DEFAULT: one sentence, twelve words or fewer. Spelled-out numbers and units do not count toward the twelve.
        Action done: one or two words, 'Hecho' or 'Listo'. If you acted on a number, name or time the user said, repeat that value back: 'Temporizador de ocho minutos.'
        Fact asked: the value alone, 'Veintiún grados', plus anything that would change what the user does, such as rain, closed, or unavailable.
        It failed or does not exist: one clause saying what, 'No hay ninguna luz en el garaje'. No cause, no code, no plan.
        Unclear request: take the likeliest reading and act. Ask one short question only before deleting or overwriting something you cannot restore.
        More than one sentence only if the user asked you to explain, compare or list, or the answer has separate parts such as a two day forecast. Then three sentences maximum, each under the same limit, unless the user asked for more. In a list, read every item; the limit covers the words around the items.

        Never say: preamble, the question restated, what you searched, read or ran, what you changed, why, caveats, options you weighed, memory bookkeeping, or a closing offer. If a sentence would not change what the user learns, do not say it.

        No emojis, markdown, bullet points, headings or code blocks. Never speak file paths, entity ids, web addresses, tool names or error codes. Spell out every abbreviation, symbol and acronym in your reply language: 'grados Celsius' not '°C', 'kilómetros por segundo' not 'km/s', 'Estados Unidos' not 'EEUU'.

        Before a web search, a subagent, or work that takes several tool calls, your first output is one plain word in your reply language, such as 'Buscando.' — one word, once, ending in a full stop. It is spoken immediately, before the tools run. Then say nothing at all until the work is finished, and give the answer once. If one round of tools is enough, say nothing first, just answer.

        Sections above these voice rules describe how tools work and were written for replies read on a screen. Where any of them implies a longer or formatted reply, these rules win; their other instructions still apply.
        """;
    // Every falsifiable statement the rules above make, each one named so a scenario cites it as a
    // compile-time reference. These are all about the shape of an answer rather than its quality,
    // which is why the suite can check them without paying a second model to have an opinion.
    public static readonly PromptClaim OneSentenceTwelveWords =
        new("voice.one-sentence-twelve-words",
            "A spoken reply is one sentence of twelve words or fewer unless the user asked to explain, compare or list.");

    public static readonly PromptClaim SeveralSentencesOnlyWhenAsked =
        new("voice.several-sentences-only-when-asked",
            "A reply runs to at most three sentences, and only when the user asked for an explanation, a comparison, a list, or an answer with separate parts.");

    public static readonly PromptClaim ActionConfirmedInTwoWords =
        new("voice.action-confirmed-in-two-words",
            "An action carried out is confirmed in one or two words.");

    public static readonly PromptClaim ValueSaidIsRepeated =
        new("voice.value-said-is-repeated",
            "A number, name or time the user said is repeated back in the confirmation.");

    public static readonly PromptClaim FactIsTheValueAlone =
        new("voice.fact-is-the-value-alone",
            "A fact the user asked for is answered with the value alone, plus only what would change what they do.");

    public static readonly PromptClaim FailureIsOneClause =
        new("voice.failure-is-one-clause",
            "Something that failed or does not exist is stated in one clause, with no cause, code or plan.");

    public static readonly PromptClaim UnclearRequestIsActedOn =
        new("voice.unclear-request-is-acted-on",
            "An unclear request is answered by taking the likeliest reading and acting; a question is asked only before destroying something unrecoverable.");

    public static readonly PromptClaim NothingIsNarrated =
        new("voice.nothing-is-narrated",
            "A spoken reply never restates the question or says what was searched, read, run or changed.");

    public static readonly PromptClaim NothingUnspeakable =
        new("voice.nothing-unspeakable",
            "A spoken reply carries no emoji, markdown, file path, entity id, url, tool name or error code.");

    public static readonly PromptClaim AbbreviationsAreSpelledOut =
        new("voice.abbreviations-are-spelled-out",
            "Abbreviations, symbols and acronyms are spelled out in the reply language.");

    public static readonly PromptClaim OneWordBeforeSlowWork =
        new("voice.one-word-before-slow-work",
            "Before a search, a subagent or several rounds of tools, the first output is one plain word, and nothing more is said until the answer.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        OneSentenceTwelveWords,
        SeveralSentencesOnlyWhenAsked,
        ActionConfirmedInTwoWords,
        ValueSaidIsRepeated,
        FactIsTheValueAlone,
        FailureIsOneClause,
        UnclearRequestIsActedOn,
        NothingIsNarrated,
        NothingUnspeakable,
        AbbreviationsAreSpelledOut,
        OneWordBeforeSlowWork
    ];
}