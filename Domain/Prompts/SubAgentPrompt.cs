namespace Domain.Prompts;

// The delegation rules (Sections/subagents.md). Every sentence there is one a claim or the voice
// override reads.
public static class SubAgentPrompt
{
    private static readonly PromptDocument _document = PromptDocuments.Section(PromptManifest.Subagents);

    public static readonly string SystemPrompt = _document.Text;

    public static readonly PromptClaim ASingleCallIsDoneInPlace =
        _document.Claim("subagents.a-single-call-is-done-in-place");

    public static readonly PromptClaim ContextBoundWorkIsNotDelegated =
        _document.Claim("subagents.context-bound-work-is-not-delegated");

    public static readonly PromptClaim PromptIsSelfContained =
        _document.Claim("subagents.prompt-is-self-contained");

    public static readonly PromptClaim SuccessCriteriaAreStated =
        _document.Claim("subagents.success-criteria-are-stated");

    public static readonly PromptClaim TheResultIsNotRedone =
        _document.Claim("subagents.the-result-is-not-redone");

    public static readonly PromptClaim AnswerIsSynthesised =
        _document.Claim("subagents.answer-is-synthesised");

    public static readonly PromptClaim NoWorkerIsNamed =
        _document.Claim("subagents.no-worker-is-named");
}