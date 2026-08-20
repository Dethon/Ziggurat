namespace Domain.Prompts;

// What a section legislates, and who it beats when somebody else legislates the same thing.
//
// A contradiction between two sections is not a wording problem to be smoothed over in prose; it
// is a question with an answer, and the answer is which section the model should apply. Stating
// it here makes the answer checkable: `PromptAssembly` reports a shared rule that nobody claimed
// to win, and the same declaration is what a test reads to prove the voice rules beat the
// screen-oriented formatting they sit above.
public sealed record ConflictPolicy
{
    // Rules from `PromptRules` this section speaks to.
    public IReadOnlyList<string> Claims { get; init; } = [];

    // Section names whose claim on a shared rule this section beats. Legal downward only: an
    // overriding section has to sit at a later priority, because later is what the model applies —
    // a section claiming to win from further up would be a statement the assembly contradicts.
    public IReadOnlyList<string> Overrides { get; init; } = [];

    public static readonly ConflictPolicy None = new();

    public static ConflictPolicy Governs(params string[] rules) => new() { Claims = rules };

    public ConflictPolicy Beating(params string[] sections) => this with { Overrides = sections };
}