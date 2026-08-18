namespace Domain.Prompts;

// One falsifiable statement a prompt section makes about what the agent will do — "a duration
// under four hours becomes a timer, never a calendar alarm". It is declared beside the prose that
// teaches it and cited by the scenarios that exercise it, so a claim nobody tests is visible
// rather than assumed (docs/adr/0031).
//
// Distinct from a prompt rule, which names the topic one section legislates so another can
// override it: a rule is a subject, a claim is an assertion.
public sealed record PromptClaim(string Id, string Statement);