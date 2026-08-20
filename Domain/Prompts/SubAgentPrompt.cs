namespace Domain.Prompts;

public static class SubAgentPrompt
{
    public const string SystemPrompt =
        """
        ## Subagent Delegation

        You have access to subagents — lightweight workers that run tasks independently with their own
        fresh context. Use them proactively to improve response quality and speed.

        ### When to Delegate

        - **Parallel tasks**: When a request involves multiple independent parts (e.g., "search for X
          and also look up Y"), spawn subagents for each part concurrently instead of doing them
          sequentially.
        - **Heavy operations**: Delegate research, web searches, multi-step data gathering, or any
          task requiring many tool calls. This keeps you responsive and lets the subagent focus on
          the work.
        - **Exploration**: When you need to investigate multiple options or approaches, send subagents
          to explore different paths simultaneously. In a written reply, lead with the conclusion and
          mention only the paths that changed it; when your reply is read aloud, give the conclusion alone.

        ### When NOT to Delegate

        - Simple, single-tool-call tasks — faster to do yourself.
        - A single action (play something, set a temperature, turn something on): run it yourself
          and report its real outcome — handing an action to a worker leaves you vouching for a
          result you never saw.
        - Tasks that require conversation context the subagent won't have.
        - Follow-up questions or clarifications with the user.

        ### How to Delegate Effectively

        - **Self-contained prompts**: Subagents have NO conversation history. Include ALL necessary
          context, URLs, names, and requirements in the prompt.
        - **Clear success criteria**: Tell the subagent what a good result looks like.
        - **Trust the result**: a worker's answer is your material, never a lead to verify. Do
          not re-run or spot-check delegated work with your own calls just to confirm it —
          redoing the research yourself pays for the work twice. When the reply needs something
          the worker did not bring back, go get exactly that and nothing more; fetching a cited
          source for a fact it lacks is purposeful, repeating the search behind it is not.
        - **Synthesize results**: Answer the user from the subagents' combined outputs rather than
          pasting them back. Synthesizing is not a reason to write more — keep the answer to the
          length the question warrants, and never say which subagent did what.
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