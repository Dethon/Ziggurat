namespace Domain.Prompts;

// Everything about a skill except its words: what it is called, the one line that advertises it,
// what the advertisement and the body may cost, which server ships it and what it claims. It sits
// in the manifest beside the sections because it is a section the model reads on demand rather
// than on every turn (docs/adr/0039): the base prompt carries only the name and description, and
// the body arrives as a tool result when the model asks for it.
//
// Deliberately carries no ConflictPolicy. A section declares the rules it governs and the sections
// it beats, and `PromptAssembly` fails a shared rule nobody claimed to win; a skill declares
// neither, so a skill body contradicting a standing section is not something the assembly can see.
// That is a known hole rather than an oversight: a skill's body arrives mid-turn, so it has no
// fixed place in the "later wins" order a `Beating(...)` claim is checked against, and giving it
// one would mean answering whether a loaded body outranks every section — a bigger decision than
// the single case that raised it.
//
// The case: the vault skill's "ask before an irreversible change" contradicts the core directive's
// "never hedge, do not gatekeep", which governs PromptRules.Refusals. Nothing reported the clash,
// and glm-5.3-flash deleted seven of the user's notes and said it was done. The exception is
// carved in CoreDirectivePrompt itself, where the unconditioned rule is stated.
//
// Patch the next one where its standing rule lives, the same way, and only widen this record if
// the cases stop being rare.
public sealed record SkillDeclaration
{
    public required string Name { get; init; }

    // The whole trigger. The model decides to load a skill from this line and nothing else, so it
    // is the one piece of a skill that is read on every turn, and the one the trigger claim is
    // about.
    public required string Description { get; init; }

    public required int DescriptionBudget { get; init; }

    public required int BodyBudget { get; init; }

    // The compose service that ships this skill. Never null: a skill teaches a server's tools and
    // is served beside them, so a deployment without the server cannot advertise it.
    public required string ServedBy { get; init; }

    // The trigger claim first — a request of this kind loads the skill — then every claim the body
    // makes. Aggregated with the sections' claims so coverage sees one set.
    public IReadOnlyList<PromptClaim> Claims { get; init; } = [];

    // False for a skill that arrived from a server the manifest says nothing about: it is still
    // offered, under the default budgets, and the assembly says so.
    public bool Declared { get; init; } = true;

    public PromptSkill Bind(string description, string body) => new(this, description, body);
}

// A skill as the session holds it: the declaration and the words a server served under its name.
public sealed record PromptSkill(SkillDeclaration Declaration, string Description, string Body)
{
    public string Name => Declaration.Name;

    public int DescriptionTokens => PromptTokens.Estimate(Description);

    public int BodyTokens => PromptTokens.Estimate(Body);

    public bool IsOverBudget =>
        DescriptionTokens > Declaration.DescriptionBudget || BodyTokens > Declaration.BodyBudget;
}

// What a server ships for one skill, built from the constants beside its declaration. The hosting
// helper turns it into the index entry and the body resource, so no server hand-writes either.
public sealed record SkillText(string Name, string Description, string Body);