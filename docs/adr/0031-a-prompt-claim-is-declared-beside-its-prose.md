# 0031 — A prompt claim is declared beside its prose and cited by the scenario that tests it

Status: accepted, amended in place on 2026-08-30 (a guard is a third coverage state — see below)
Date: 2026-08-18

## Context

A prompt cannot stop being prose — the model reads it. So an executable regression test is
always a *second* statement of the same rule, and the two drift: somebody edits
`HomeAssistantPrompt.cs` and the scenario keeps asserting last month's contract, still green.

`PromptRules` already exists and is not this. Its constants (`formatting`, `verbosity`,
`tool-use`, `memory`, `refusals`) name the **topic** a section legislates, so another section can
override it — one subject per constant, six in total. What a scenario needs to cite is far finer
and of a different kind: `TimerPrompt` alone makes about eight separately falsifiable statements.
Overloading `PromptRules` with them would confuse arbitration with verification.

## Decision

A **claim** is one falsifiable statement a prompt section makes about what the agent will do. It
is declared beside the prose that teaches it — `TimerPrompt.Claims` holds `(id, one-line
statement)` — and `PromptManifest` aggregates claims across sections the way it already
aggregates declarations. Every scenario cites the claim ids it exercises, and a coverage test
fails when a declared claim has no scenario.

Every claim is declared up front, including the ones nothing tests yet; the uncovered ones sit in
an explicit exemption list with a reason. Declaring claims only where scenarios already exist
would keep the suite green and leave the untested claims — the ones that most need writing
down — undeclared forever.

A scenario counts only once it has been **demonstrated red** by deleting the prose of the claim
it cites, with the result noted in the commit that adds it. A scenario that stays green with its
own claim's prose removed is measuring the model's default behaviour, not this repo's prompt, and
protects nothing.

## Consequences

`PromptRules` and claims are separate vocabularies with separate jobs, and `CONTEXT.md` records
the distinction so the next reader does not merge them: a rule is a subject, a claim is an
assertion.

Adding a rule to a prompt now costs a declared claim, and either a scenario or a line in the
exemption list. That is the friction the decision is buying: the exemption list is the backlog,
and it is visible.

## Amendment (2026-08-30) — a guard is a third coverage state

The two states above turned out not to be enough. 26 of the 29 exemption entries described the
same third thing: a scenario runs and asserts the claim's behaviour but cannot earn the citation,
because the demonstrated-red bar was tried and deleting the prose stayed green — the behaviour is
the model's own rather than the prompt's. Those facts lived in a static table keyed by claim id,
a file away from both the claim and the scenario, restated on the scenario side as a comment
ending in a hyperlink; neither statement could check the other, and every scenario change dragged
an edit through the table.

A **guard** is now the scenario's own declaration: `Scenario.Guards` lists the claims it asserts
without evidencing, each with a mandatory note recording the demonstration. The claim id is a
compile-time symbol, so a renamed or withdrawn claim breaks the scenario that guards it, in the
file that guards it. More than one scenario may guard the same claim. The guard lives on the
scenario and not on the claim because the dependency direction allows nothing else: claims are
declared in `Domain`, scenarios in `Tests.Eval`, and a claim-side back-reference could only ever
be prose — which is the shape being removed.

Coverage becomes a precedence, not a partition: `cited > judged > conditional > guarded >
exemption > uncovered`. A claim both cited and guarded is a truth — one scenario demonstrated
red, another asserts the same claim as a side condition — so no test forbids the pair; the
citation simply outranks the guard in the scorecard.

The exemption list survives for what no scenario touches at all, and that list is not empty: a
claim can be guarded *diffusely* — asserted by every scenario's exhaustive permitted set, or by
every spoken scenario's reply limit — and owned by none, and assigning such a guard to one
scenario would be arbitrary. Those entries stay exemptions under the kind their prose earns.
Kinds with no entries are deleted rather than kept as documentation; a kind returns with its
first user.

"Either a scenario or a line in the exemption list" above now reads: cited, guarded, or exempted.
Everything else stands — every claim still costs a scenario or a written reason, a citation still
costs a demonstration red, and the exemption list is still the backlog in the open. What changed
is that a guard is not backlog: the claim is tested every run, it just proves the model rather
than the prompt. The exemption-versus-cited contradiction test survives shrunk, policing only the
residual list, and `CONTEXT.md` pins **Guard** and **Exemption** so the two stop being called by
each other's names.
