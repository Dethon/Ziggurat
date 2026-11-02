namespace Domain.Monitor;

// What a person is told when the agent refuses their turn, spelled once for the agent that answers it
// and for a channel that says it in its own language: voice speaks each of these as a Spanish sentence
// of its own (McpChannelVoice's SpokenRefusals), because this prose spoken by a Spanish voice is noise.
// A refusal added here is one voice must learn to say, which its tests ask of every one in All.
public static class RefusalAnswers
{
    // A person of a household the operator suspended. Nothing they can do here changes it.
    public const string Suspended =
        "Your household is suspended, so its assistant does not answer until whoever runs this deployment resumes it.";

    // A person whose turn was refused. Who they are is the one thing the agent could not believe, so it
    // says no more than that; the reason is on the errors feed.
    public const string NotVerified = "This message could not be verified as yours, so it was not answered.";

    // A member an owner disabled: who they are was believed, and the household says the agent acts for
    // them no more. Whoever disabled them can say why.
    public const string Disabled =
        "Your account in this household is disabled, so this message was not answered. Ask the household's owner.";

    // Whether their household runs, or still lets them in, could not be read just now: never that it is
    // suspended or that they are disabled, which may not be so.
    public const string Unchecked =
        "Whether your household lets this message through could not be checked right now, so it was not answered. Try again in a moment.";

    // A household that set no OpenRouter key: nothing was asked of any model, and nothing will be until a
    // key is added, which they cannot do from here.
    public const string WithoutKey =
        "Your household has no OpenRouter key set, so its assistant cannot answer yet. Ask whoever runs it to add one.";

    // A satellite in a household with no voice agent (HouseholdAgents.VoiceAgent): where to pick one of
    // its agents to answer aloud, or add one from the voice template.
    public const string NoVoiceAgent =
        "Your household has no voice agent yet, so nobody answers here. An owner can pick one on the Agents page, or add one from the Nabu template.";

    public static IReadOnlyList<string> All { get; } =
        [Suspended, NotVerified, Disabled, Unchecked, WithoutKey, NoVoiceAgent];
}