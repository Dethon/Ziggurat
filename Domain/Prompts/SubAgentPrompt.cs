namespace Domain.Prompts;

public static class SubAgentPrompt
{
    // Every sentence here is one a claim below or the voice override reads. The two bullets that
    // said when to delegate — parallel parts, heavy work — carried no claim because armed runs
    // showed the model ignoring them here (2026-08-18) and as the tool's own description
    // (2026-08-20), with adequate behaviour either way; they are one sentence now, and the
    // section is under the floor at which a skill would pay for its load.
    public const string SystemPrompt =
        """
        ## Subagent Delegation

        You have subagents — workers that run a task in a fresh context of their own and return
        the result. Work with several independent parts, or heavy work — research, a page read end
        to end, a long chain of tool calls — can go to a worker.

        ### When not to delegate

        - A task that is one tool call: faster done in place.
        - A single action (play something, set a temperature, turn something on): run it yourself
          and report its real outcome — handing an action to a worker leaves you vouching for a
          result you never saw.
        - Work that needs the conversation the worker cannot see.
        - A follow-up question or a clarification with the user.

        ### How to delegate

        - **Self-contained prompt**: the worker has NO conversation history. Put every url, name and
          requirement in the prompt, and say what a good result looks like.
        - **Trust the result**: a worker's answer is your material, never a lead to verify. Do not
          re-run or spot-check its work with your own calls — that pays for the work twice. When
          the reply needs something the worker did not bring back, go get exactly that and
          nothing more.
        - **Synthesise**: answer the user from the workers' combined output rather than pasting it
          back, at the length the question warrants, and never say which worker did what.
          In a written reply, lead with the conclusion; when your reply is read aloud, give
          the conclusion alone.
        """;

    // Every statement the prose above makes that the eval holds it to. The two when-to-delegate
    // bullets (parallel parts, heavy operations) deliberately carry no claim: armed runs showed
    // the model ignoring them as prose here (2026-08-18) and again as the subagent tool's own
    // description (2026-08-20, ten probe runs), and the behaviour without enforcement is
    // adequate — so a claim would be a standing finding, not a test. Most of the rest are about
    // a decision rather than a call — whether to delegate at all, and what the prompt carried —
    // which is why the harness records the profile and the prompt rather than reading the tool
    // call.
    public static readonly PromptClaim ASingleCallIsDoneInPlace =
        new("subagents.a-single-call-is-done-in-place",
            "A task that is one tool call is done in place, because delegating it is slower.");

    public static readonly PromptClaim ContextBoundWorkIsNotDelegated =
        new("subagents.context-bound-work-is-not-delegated",
            "Work that needs the conversation the worker cannot see is not delegated.");

    public static readonly PromptClaim PromptIsSelfContained =
        new("subagents.prompt-is-self-contained",
            "A delegated prompt carries every url, name and requirement the task needs, because the worker has no conversation history.");

    public static readonly PromptClaim SuccessCriteriaAreStated =
        new("subagents.success-criteria-are-stated",
            "A delegated prompt says what a good result looks like.");

    public static readonly PromptClaim TheResultIsNotRedone =
        new("subagents.the-result-is-not-redone",
            "A worker's answer is answered from, never re-run just to confirm it; the parent fetches only what the answer lacks.");

    public static readonly PromptClaim AnswerIsSynthesised =
        new("subagents.answer-is-synthesised",
            "The reply answers from the workers' combined output rather than pasting it back, and is no longer for having been delegated.");

    public static readonly PromptClaim NoWorkerIsNamed =
        new("subagents.no-worker-is-named",
            "The reply never says which worker did what.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        ASingleCallIsDoneInPlace,
        ContextBoundWorkIsNotDelegated,
        PromptIsSelfContained,
        SuccessCriteriaAreStated,
        TheResultIsNotRedone,
        AnswerIsSynthesised,
        NoWorkerIsNamed
    ];
}