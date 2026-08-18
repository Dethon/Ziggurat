# 0031 — A prompt claim is declared beside its prose and cited by the scenario that tests it

Status: accepted
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
