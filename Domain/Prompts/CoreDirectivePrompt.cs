namespace Domain.Prompts;

// The section every agent reads first. Its words, its declaration and its claims are its document's
// (Sections/core_directive.md); this is the handle the code and the scenarios hold them by. A claim
// handle resolves from the document by id, so a claim the document stops declaring fails the first
// test that touches it, in the file that cites it.
public static class CoreDirectivePrompt
{
    public const string Name = "core_directive";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string Instructions = _document.Text;

    public static readonly PromptClaim NoPlaceholderToolCalls =
        _document.Claim("core.no-placeholder-tool-calls");

    public static readonly PromptClaim NoStepIsAnnouncedBesideACall =
        _document.Claim("core.no-step-is-announced-beside-a-call");
}