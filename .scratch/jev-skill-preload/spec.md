# Jev skill preload

Status: ready-for-agent

Grilled 2026-09-18. Glossary: `CONTEXT.md` § Prompt (**Skill**, **Trigger claim**, **Preload**).
Decision: `docs/adr/0039-…` § Refined 2026-09-18. Probe and its results: `probe/`.

First of five uses of TypeSafe's Jev in this repo, in this order: skill preload (this spec), the
memory pipeline, the delegation hint, modal dismissal, voice approval. This spec also builds what
the other four share: the typed-judgment contract and the one client that speaks to TypeSafe.

## Problem Statement

When Francisco asks Nabu to turn on a light, the model's first round trip is spent on nothing he
asked for: it reads seven one-line descriptions, decides the home skill applies, and calls
`load_skill`. Only the second round trip touches the house. ADR 0039 measured that hop at about
1.15 s and accepted it, naming a bounded pre-load as the fallback if it hurt. It hurts most where
a person is standing in a room waiting for a lamp.

The choice is also the weakest step of the turn. It is one-of-seven-or-none made by a general
model from a list at the tail of 9,500 tokens, and the eval's recurring reds on the shipped model
are skipped steps — a load not made is the first step to skip.

## Solution

A **preload**: before the model's first call, the host asks Jev — a small hosted model that
answers typed questions with probabilities and generates no text — which skills this request
needs, and when Jev is sure, puts the skill's body into the conversation exactly as a
`load_skill` call would have left it. The model's first round trip is then the task.

Jev is asked two things about the request in one call: which one skill it needs first, or none
(a choice), and, per skill, whether it needs that skill (a yes/no each). The winner of the choice
is preloaded at 0.9 confidence; any other skill whose own yes/no reaches 0.9 rides along; two
bodies at most; a confident "none" preloads nothing. Below the bar nothing happens and the model
loads for itself as it does today — the load tool stays, and a preload is only ever a head start.

The call runs while memory recall runs, so a live turn pays nothing for it, and under a deadline,
so TypeSafe being slow or down costs a turn nothing either. A turn addressed to the local box
never reaches Jev.

Measured on 2026-09-18 against the seven shipped descriptions, 34 requests in Spanish and English
(`probe/`): the choice picked right on 32, and both misses fell under 0.9; at the 0.9 bar no wrong
preload in either shape; 7 of 7 no-skill requests answered "none". About 110 ms at the server,
about 380 ms from this network on a warm connection with a tail past a second, 2.2k input tokens
a call — about a hundredth of a cent.

## User Stories

1. As a person at a satellite, I want "enciende la luz del salón" to reach the house one round trip sooner, so that a skill load is not something I wait through.
2. As a person, I want a turn that needs no skill to be exactly as fast as today, so that the preload never costs what it does not save.
3. As a person, I want a turn to be answered when TypeSafe is down, slow or rate-limited, exactly as it is answered today, so that a third party can never fail my turn.
4. As a person, I want a request that needs two skills — "busca una receta y guárdala en mis notas" — to get both when Jev is sure of both, so that the head start is not limited to one.
5. As a person, I want a skill that is already in the conversation never put there again, so that a long conversation does not fill with copies of the home guide.
6. As a person, I want what I say to the local box to stay on the local box, so that the boundary ADR 0042 drew for extraction holds for Jev.
7. As a person, I want a wrong guess to be rare and harmless — an unneeded body in the conversation, never a wrong action — so that the bar is set for precision.
8. As a person delegating work, I want a worker to get its skill the same way, so that a delegated task also starts on the task.
9. As the maintainer, I want the thresholds, the cap, the deadline and the pinned Jev model in `appsettings.json`, so that tuning is a config edit with a probe and an eval pass behind it.
10. As the maintainer, I want the key to be a secret like every other, so that it appears in no file as a value.
11. As the maintainer, I want one client that talks to TypeSafe and one contract the Domain asks it through, so that the four uses that follow add questions, not plumbing.
12. As the maintainer, I want Jev to judge by the description the model reads, so that a skill has one advertisement and a red trigger claim has one fix.
13. As the maintainer, I want a trigger claim to be satisfied by whoever loaded the skill, and the scorecard to say who did, so that I still know whether a description works on the model when Jev abstains.
14. As the maintainer, I want a preload outside a scenario's permitted set to redden it, so that a wrong preload is as visible as a wrong load.
15. As the maintainer, I want Jev's spend on the scorecard beside the model's, so that the eval never spends money it does not report.
16. As the maintainer, I want an eval run with no TypeSafe key to run with the preload off and say so, so that a missing key is a labelled scorecard and not a failure.
17. As the operator, I want each Jev call published as one event — outcome, skills, confidences, latency, tokens — and a dashboard panel for preload rate and deadline misses, so that a TypeSafe degradation is something I can see.
18. As the maintainer, I want the probe kept as a repeatable script with its labelled set, so that a Jev version bump or a description edit is checked in a minute for cents.

## Decisions

### What is asked

- One request per turn. State: `{"request": <the current user message's text>}` — no prior turns,
  no assistant text. Jev's accuracy falls with irrelevant state, and a follow-up's skill is
  normally already loaded.
- Questions, over the skills the session advertises **minus those already loaded**:
  - `skill`: a choice whose criteria are each skill's shipped `Description` verbatim, plus `none`.
  - `needs_<name>`: one noul per skill, the description in its instructions.
  - Instructions say "request", never "what a person said": a worker's request is a delegation
    prompt.
- If every advertised skill is already loaded, or the session has no skills, no call is made.
- Already loaded is read off the conversation: a `load_skill` call naming the skill, whoever made
  it (or the fallback form, below). There is no second record to drift.

### The rule

`SkillPreloadPolicy`, a pure function of the answers and the settings:

1. `skill` = `none` with confidence ≥ `NoneVeto` (0.9) → nothing.
2. `skill` = s with confidence ≥ `ChoiceConfidence` (0.9) → s.
3. Every other skill with `needs_<name>` ≥ `NoulProbability` (0.9) → added, highest first.
4. Truncated to `MaxSkills` (2).

All four in `appsettings.json` under `skillPreload`, beside `enabled` and `deadlineMs` (600).

### Where it happens

- `ISkillPreloader` (Domain) is the one service. It is started in `ConversationGroup` where the
  user message is built, concurrently with `IMemoryRecallHook.EnrichAsync`, and the pending result
  rides on the message. It is nullable there exactly as the recall hook is.
- `SkillsProvider` is the one insertion point. It takes the pending result from the message — or,
  where nothing started one (an eval run, a subagent), asks the preloader itself — waits no longer
  than what is left of the deadline, and returns the preload as `AIContext.Messages`, which the
  history provider persists.
- A deadline miss, a transport error, a 429 or a 529 is no preload. Nothing is retried inside a
  turn and nothing is thrown into it.
- A Lemonade turn (`LemonadeModelId.IsLemonade` on the turn's config patch) makes no call. The
  gate fails closed, as recall's does.

### What is inserted

- An assistant message carrying one `FunctionCallContent` per preloaded skill — the load tool's
  name, `skillName` — and a tool message carrying each `FunctionResultContent` with the body
  wrapped exactly as the framework's load returns it. No reasoning content. After the user
  message, so the cached prefix is untouched.
- Whether every deployed model accepts a function call it never emitted is not known and is the
  first ticket. **The fallback** where one does not: a single persisted message carrying the same
  wrapped body; the already-loaded check recognises that form too, and the `skills` section gains
  one sentence — a skill body already in the conversation is loaded, never load it again. The
  form is chosen per host by what the spike finds, not per turn.

### The client and the contract

- Domain: a typed-judgment contract — a state (string or JSON), named questions (choice, noul;
  score is added by the first use that needs one), typed answers carrying probabilities and
  confidence, and usage. It knows nothing of HTTP or of TypeSafe.
- Infrastructure: one client. `POST https://api.typesafe.ai/v1/systemone`, bearer key, one pooled
  warm connection (a cold handshake measured 560 ms), a per-call deadline from the caller, 401 and
  422 as configuration errors that log loudly once, 429/529 as absence. No .NET SDK exists; this
  is a typed `HttpClient`.
- Settings: `typeSafe: { apiUrl, apiKey, model }`. `model` is pinned to `jev-1.13.0` — what
  `jev-latest` resolved to when the probe ran; `jev-1.13` is refused by the API. A bump is a
  deliberate edit with a probe run and an eval pass behind it. `apiKey` is a secret: placeholder
  in `DockerCompose/.env`, `${TYPESAFE_API_KEY}` in compose, user secrets in development. An empty
  key is the feature off, not a startup failure.

### The eval

- The eval preloads: it builds the agent through production DI, and the provider's fallback path
  makes the call because no `ConversationGroup` ran.
- `Recording` learns of a preload through its own observation, marked as the host's. A
  `LoadsSkill` expectation is met by either loader; a preloaded skill outside the scenario's
  permitted set reddens it like any wrong load.
- The scorecard carries, per scenario and per pass, who loaded (host / model / nobody), Jev's
  tokens and cost in `spend`, and the Jev model beside the agent model.
- The key is read from user secrets beside the OpenRouter key. Absent: preload off, the scorecard
  says `preload: off`, nothing fails.

### Telemetry

- One `SkillPreloadEvent` per call through `IMetricsPublisher`: agent, channel, outcome
  (`preloaded`, `abstained`, `none`, `deadline`, `error`, `skipped-lemonade`), skills, the
  confidences, latency, input tokens. A dashboard panel: outcome share over time, and latency.
- A preload emits no `ToolCallEvent`; tool-call counts keep meaning "the model called it".

### Acceptance

Full tier, `ZIGGURAT_EVAL_EXHAUSTIVE=1`, on the shipped model and on
`ZIGGURAT_EVAL_MODEL=openai/gpt-5.6-luna`, once with `skillPreload.enabled` false and once true —
four scorecards, each backed up before the next run. Mergeable when, on both models: the claim
pass rate does not drop, `SkillNotLoaded` does not rise, no scenario is red on a wrong preload,
and input tokens per scenario fall. No build while a pass runs.

## Out of Scope

- Prior turns in Jev's state, and preloading on follow-ups by context.
- A Jev-specific criteria text per skill; overlaps between descriptions are fixed in the
  descriptions.
- Removing `load_skill`, or the `skills` section's load instructions.
- Any other use of Jev — each has its own spec and reuses the client built here.
- A per-agent opt-in. The Lemonade gate is the only boundary.
- Retrying, circuit breaking or caching Jev answers.
