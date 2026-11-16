namespace Domain.Prompts;

// The reply language, chosen from its document (Sections/language.md): the main body for an agent
// with no pinned language, a part per language with a body of its own, and the 'named' part for
// any other. Why each body says what it says is in the document, beside the words.
public static class LanguagePrompt
{
    private static readonly PromptDocument _document = PromptDocuments.Section(PromptManifest.Language);

    private static readonly string _spanish = _document.Part("es").Fill();

    private static readonly string _english = _document.Part("en").Fill();

    private static readonly PromptTemplate _named = _document.Part("named", "language");

    // Values with no body here are used verbatim as the language name, so configure a
    // human-readable name ("Galician", "Brazilian Portuguese") rather than a bare code.
    private static readonly Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = _spanish,
        ["es-ES"] = _spanish,
        ["spanish"] = _spanish,
        ["español"] = _spanish,
        ["espanol"] = _spanish,
        ["castellano"] = _spanish,
        ["en"] = _english,
        ["en-US"] = _english,
        ["en-GB"] = _english,
        ["english"] = _english
    };

    public static readonly PromptClaim ReplyFollowsTheMessage =
        _document.Claim("language.reply-follows-the-message");

    public static string Build(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return _document.Text;
        }

        var trimmed = language.Trim();
        return _pins.TryGetValue(trimmed, out var pinned) ? pinned : _named.Fill(("language", trimmed));
    }
}