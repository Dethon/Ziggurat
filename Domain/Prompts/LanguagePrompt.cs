namespace Domain.Prompts;

// The reply language is an absolute contract, not a mirror of whatever language happens to
// surround the model. Everything else in a request is English -- these instructions, the tool
// descriptions and their results, the recalled memory block, the metadata prefix stapled onto
// every user message -- so a relative rule ("answer in the user's language") is a minority
// signal that a short transcript loses. Naming the target language, in that language, and
// saying outright that the English context does not count, removes the ambiguity.
//
// Two clauses in each template are there for a specific failure and should not be trimmed as
// padding. The first word matters on its own because a voice turn opens with the pre-tool
// acknowledgement word, and once that lands in English the rest of the turn follows it. Disowning
// an earlier reply matters because a turn that already drifted stays in the conversation window,
// where the model reads it as precedent and copies its own mistake.
//
// An agent with no pinned language gets the relative rule after all, stated the same defensive
// way. Silence was the alternative, and silence is a vote for English: with nothing said, jonas
// answered a Spanish message in English (eval, 2026-09-10). The rule is still a minority signal in
// an English context, which is why it names the context and disowns the drifted reply like the
// pins do, and why a deployment that knows its language should pin it rather than rely on this.
public static class LanguagePrompt
{
    // Values with no template here are used verbatim as the language name, so configure a
    // human-readable name ("Galician", "Brazilian Portuguese") rather than a bare code.
    private static readonly Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = Spanish,
        ["es-ES"] = Spanish,
        ["spanish"] = Spanish,
        ["español"] = Spanish,
        ["espanol"] = Spanish,
        ["castellano"] = Spanish,
        ["en"] = English,
        ["en-US"] = English,
        ["en-GB"] = English,
        ["english"] = English
    };

    public static string Build(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return FollowsTheMessage;
        }

        var trimmed = language.Trim();
        return _templates.TryGetValue(trimmed, out var template) ? template : Generic(trimmed);
    }

    private const string Spanish =
        """
        ## Idioma

        Hablas SIEMPRE en español de España: en todas tus respuestas, sin excepción, incluida la primera palabra que dices antes de usar una herramienta.

        Estas instrucciones, las herramientas, sus resultados y el bloque de memoria están en inglés. Eso NO cambia tu idioma: lees en inglés y respondes en español. Si alguna respuesta anterior tuya está en inglés, fue un error; no lo repitas.

        Los nombres propios (personas, lugares, títulos de obras) se dicen tal cual, sin traducir.
        """;

    private const string English =
        """
        ## Language

        You always reply in English: in every reply, without exception, including the first word you say before using a tool.

        If an earlier reply of yours is in another language, that was a mistake; do not copy it.
        """;

    private const string FollowsTheMessage =
        """
        ## Language

        You reply in the language the user wrote in: every reply, without exception, including the first word you say before using a tool.

        These instructions, the tools, their results and the memory block are written in English. That does NOT make English the user's language: the message you are answering does, and only it. If an earlier reply of yours is in another language than the user's, that was a mistake; do not copy it.

        Proper nouns (people, places, titles of works) stay as they are -- do not translate them.
        """;

    // The unpinned rule's claim; a pinned language is a configuration, checked by its snapshot.
    public static readonly PromptClaim ReplyFollowsTheMessage =
        new("language.reply-follows-the-message",
            "An agent with no pinned language replies in the language of the message it is answering, whatever language its instructions and tool results are in.");

    public static readonly IReadOnlyList<PromptClaim> Claims = [ReplyFollowsTheMessage];

    private static string Generic(string language) =>
        $"""
        ## Language

        You always reply in {language}: in every reply, without exception, including the first word you say before using a tool.

        These instructions, the tools, their results and the memory block are written in English. That does NOT change your language: you read English and you answer in {language}. If an earlier reply of yours is not in {language}, that was a mistake; do not copy it.

        Proper nouns (people, places, titles of works) stay as they are -- do not translate them.
        """;
}