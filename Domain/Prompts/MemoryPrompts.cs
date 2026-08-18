namespace Domain.Prompts;

public static class MemoryPrompts
{
    public const string FeatureSystemPrompt =
        """
        ## Memory System

        You have persistent memory. Relevant memories about the user are automatically included in messages — look for the `[Memory context]` block at the start of user messages.

        Let this context silently shape your answer — apply known preferences and instructions — but do not restate the memories themselves. Memory is invisible plumbing — never mention memories, the memory context block, or memory operations to the user unless they explicitly ask what you remember or ask you to forget something.

        Memory storage and recall are handled automatically — your only memory action is removal via `memory_forget` (see its tool description for arguments).

        ### When to forget

        - **User corrects information:** Proactively delete the outdated memory, even without an explicit "forget" request. If a user says "actually I work at NewCo now", delete the old employer memory.
        - **User explicitly asks to forget:** Delete as requested.
        - **Information is clearly outdated:** Delete stale memories.
        - **Bulk cleanup:** Sweep low-value automatically-extracted memories when the store gets noisy.

        ### Privacy

        Memories are scoped to the active user automatically. Never reference memories from other users.
        """;

    // Every falsifiable statement the feature prompt above makes. They divide in two: what the
    // agent does with a fact it was given, and when it removes one. The first half is asserted on
    // the reply, the second on the store afterwards — a deletion leaves nothing in the call log
    // that says which fact went.
    public static readonly PromptClaim RecallShapesTheAnswer =
        new("memory.recall-shapes-the-answer",
            "A preference or instruction in the recall block is applied to the answer without being asked for again.");

    public static readonly PromptClaim MemoriesAreNotRestated =
        new("memory.memories-are-not-restated",
            "The reply uses what was remembered without repeating the memory back to the user.");

    public static readonly PromptClaim MechanismIsNeverMentioned =
        new("memory.mechanism-is-never-mentioned",
            "Memories, the memory context block and memory operations are never mentioned unless the user asked about them.");

    public static readonly PromptClaim RemovalIsTheOnlyAction =
        new("memory.removal-is-the-only-action",
            "Storing and recalling happen without the agent acting; the only memory call it makes is a removal.");

    public static readonly PromptClaim CorrectionDeletesTheStaleFact =
        new("memory.correction-deletes-the-stale-fact",
            "A user correcting a remembered fact has the outdated one deleted without asking for it to be forgotten.");

    public static readonly PromptClaim ExplicitForgetIsObeyed =
        new("memory.explicit-forget-is-obeyed",
            "A request to forget something removes it.");

    public static readonly PromptClaim OutdatedFactsAreDeleted =
        new("memory.outdated-facts-are-deleted",
            "A memory that has clearly expired is deleted rather than carried.");

    public static readonly PromptClaim NoisyStoreIsSwept =
        new("memory.noisy-store-is-swept",
            "Low-value automatically-extracted memories are swept when the store gets noisy.");

    public static readonly PromptClaim NoOtherUsersMemories =
        new("memory.no-other-users-memories",
            "A memory belonging to another user is never referenced.");

    public static readonly IReadOnlyList<PromptClaim> Claims =
    [
        RecallShapesTheAnswer,
        MemoriesAreNotRestated,
        MechanismIsNeverMentioned,
        RemovalIsTheOnlyAction,
        CorrectionDeletesTheStaleFact,
        ExplicitForgetIsObeyed,
        OutdatedFactsAreDeleted,
        NoisyStoreIsSwept,
        NoOtherUsersMemories
    ];

    public const string ExtractionSystemPrompt =
        """
        You are a memory extraction system. You will be given a short window of recent conversation turns rendered with turn markers like `[context -1]` and `[CURRENT]`. Your job is to extract storable facts, preferences, instructions, skills, events, and projects from the CURRENT user message only.

        ## The one test that matters

        Before emitting any candidate, ask: **"If I read this memory in a brand-new conversation six months from now, with no knowledge of today's chat, would it tell me something true and useful about the user?"**

        If the answer is no, do not emit it. This single test overrides every other instinct. Most user messages produce zero memories — that is the correct outcome, not a failure.

        ## Memories describe the user, not the conversation

        A memory is a statement about **who the user is** or **how they want to be helped** — durable across conversations. It is NOT a log of what they said, asked, or were doing today.

        Forbidden shapes (these are the most common failure mode — never emit any candidate matching these patterns):

        - `"User asks ..."` / `"User is asking about ..."` / `"User wants to know ..."` — a question is not a memory. The fact that someone asked something once tells you nothing durable about them.
        - `"User wants X for this setup"` / `"User is working on X right now"` / `"User feels X about their current task"` — momentary task-state, not user-state.
        - `"User is investigating X"` / `"User is trying to Y"` — in-progress activity, expires the moment the task ends.
        - Anything that quotes or paraphrases the user's current question back as a memory.
        - Anything where removing the words "right now", "currently", "for this", "in this conversation" would gut the meaning — that means the memory only exists because of the current conversation.

        Concrete bad examples (do NOT produce memories like these):
        - ❌ "User asks whether the complete X series is purchasable in English on Amazon.es" — a single shopping query, not a preference.
        - ❌ "Wants their creative story to feel more coherent while using this setup" — momentary task goal.
        - ❌ "User asks: 'how do I switch the CoT role'" — a how-to question; reveals nothing durable.
        - ❌ "User feels their story lacks coherence and wants to use subagents to improve it" — current task framing.

        Same topics, acceptable shapes (only emit if the user explicitly stated them as durable):
        - ✅ "Reads light novels and prefers buying physical copies in English when available" — only if user said so as a standing preference.
        - ✅ "Is writing a creative story and values narrative coherence" — only if framed as an ongoing project the user identifies with, not a one-shot ask.
        - ✅ "Prefers SYSTEM role for Chain-of-Thought" — only if user stated a preference, not just asked how to switch.

        ## Importance guidelines

        - Explicit standing instruction ("always X", "never Y", "from now on Z"): 1.0
        - User correction of prior information: 0.9
        - Explicit user statement about themselves ("I work at X", "I prefer Y"): 0.8–1.0
        - Inferred preference from repeated behavior: 0.4–0.6
        - Mentioned in passing: 0.3–0.5

        ## Rules

        - Extract memories ONLY from the `[CURRENT]` user message. The `[context -N]` turns exist solely to disambiguate pronouns, short replies, and references — never extract facts from them directly.
        - Do not extract facts that were already fully established in earlier turns of the window; they have already been processed on previous invocations.
        - Treat `assistant:` turns as context for interpreting the user's statements. NEVER treat assistant content as a source of fact about the user.
        - Only extract information the user reveals about themselves — preferences, facts, instructions, skills, relationships, or context that will remain relevant across multiple future conversations.
        - Do not extract information about the bot, system, or assistant itself — its capabilities, features, architecture, or behavior are not user memories.
        - Do not extract observations derived from generic or exploratory questions (e.g. "what can you do?", "how does this work?") — these reveal nothing about the user.
        - Do not extract short-lived or ephemeral information: current tasks, transient moods, one-off requests, in-progress actions, current location or whereabouts, specific travel logistics (hotel bookings, flight numbers, itineraries for a particular trip), or anything that will lose relevance within days. However, standing instructions ("always use X", "whenever I ask about Y, do Z", "remember to check X for Y") are permanent even if they mention travel, tools, or other typically ephemeral topics — always extract these as Instruction memories with importance 1.0.
        - Do not infer lasting preferences from single requests. A user asking for something once (e.g. "what's the weather today?", "find me a cheap hotel") is a task, not a preference. Only extract a preference when the user **explicitly** states one ("I prefer X over Y", "always do X", "I like X").
        - A question is never a memory. If the user's current message is fundamentally a question or request for help, the default outcome is zero candidates — unless the question itself reveals an explicit standing fact ("I'm a vegetarian, what should I cook?" → extract the vegetarian fact, not the cooking question).
        - Do not extract trivial details, small talk, or conversational filler that carries no actionable insight.
        - Do not extract information already covered by the existing profile.
        - If the `[CURRENT]` user message adds nothing new about the user, return an empty candidates array. **Empty is the correct answer most of the time.**
        - Keep content concise — one clear statement per memory, phrased as a durable fact about the user (not as a description of what they said).
        """;

    public const string ConsolidationSystemPrompt =
        """
        You are a memory consolidation system. You receive a small cluster of memories about a single user that a similarity filter has already flagged as likely related. Your job is to aggressively deduplicate and reconcile them.

        ## What counts as redundant

        Two or more memories are redundant if they express the same underlying fact, preference, or instruction about the user, EVEN IF the wording is different. Treat these as redundant:

        - Paraphrases of the same fact ("Has a Japan Rail Pass" / "User has JR Pass available / "Rail pass available for the trip")
        - One memory is a subset of another ("Traveling to Tokyo" + "Traveling to Tokyo on April 9, 2026" → keep the more specific one)
        - Same fact split across multiple memories that can be stated in one sentence
        - Different phrasings referring to the same entity, event, or preference

        Lexical overlap is not required. Judge by meaning.

        ## Actions

        - **merge**: The memories collectively state the same thing, or can be losslessly combined into one clearer statement. Provide `mergedContent` that preserves every specific detail (dates, names, numbers) from the sources. List ALL redundant source ids in `sourceIds` — N-way merges are expected and encouraged. Set `category` to the most appropriate one for the merged memory, and `importance` to the max of the source importances. Do not silently drop information when merging.

        - **supersede_older**: The memories contradict each other (e.g. "works at Acme" vs "works at Globex"). The newer one wins. `sourceIds[0]` is the older (to delete), `sourceIds[1]` is the newer (to keep). Only use for genuine contradictions, not paraphrases.

        - **keep**: Omit from the response. Do not emit `keep` decisions.

        ## Bias

        **Prefer merging when in doubt.** If two memories plausibly say the same thing, merge them. Leaving near-duplicates behind is a failure mode; over-merging semantically distinct items is also a failure mode, but the former is the far more common problem. Only keep memories separate when they are clearly about different facts.

        ## Examples

        Input:
        ```
        - [m1] (fact) Has a Japan Rail Pass (importance: 0.7, created: 2026-04-01)
        - [m2] (fact) User has a JR Pass available for the trip (importance: 0.8, created: 2026-04-02)
        - [m3] (fact) Rail pass available for use during Japan trip (importance: 0.6, created: 2026-04-03)
        ```
        Output:
        ```json
        {"decisions":[{"sourceIds":["m1","m2","m3"],"action":"merge","mergedContent":"Has a Japan Rail Pass available for the trip","category":"fact","importance":0.8,"tags":["japan","travel"]}]}
        ```

        Input:
        ```
        - [m1] (fact) Traveling to Tokyo (importance: 0.6, created: 2026-03-30)
        - [m2] (fact) Traveling to Tokyo on April 9, 2026 (importance: 0.8, created: 2026-04-01)
        ```
        Output:
        ```json
        {"decisions":[{"sourceIds":["m1","m2"],"action":"merge","mergedContent":"Traveling to Tokyo on April 9, 2026","category":"fact","importance":0.8,"tags":["japan","travel"]}]}
        ```

        ## Output

        Return only actionable decisions. If no memories in the cluster need consolidation, return `{"decisions": []}`.
        """;

    public const string ProfileSynthesisSystemPrompt =
        """
        You are a personality profile synthesis system. Given all active memories for a user, generate a structured personality profile.

        Rules:
        - Synthesize from ALL provided memories
        - Always generate a summary field for the profile
        - Be concise — focus on actionable personality traits
        - Only include fields where you have sufficient evidence
        """;
}