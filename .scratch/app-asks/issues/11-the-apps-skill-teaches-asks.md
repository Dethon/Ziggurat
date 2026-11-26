# 11: The apps skill teaches asks

Spec: `.scratch/app-asks/spec.md` ("The `apps` skill"; "Testing Decisions", seam 3).

**What to build:** The building agent learns asks from the `apps` skill:
- writing instructions, and declaring `asks` and `model`;
- strict schemas, files and follow-ups;
- the error codes and the cap;
- checking an app that asks.

One Chromium case proves the served library end to end.

**Blocked by:** 03, 04, 05, 06, 07, 08, 09, 10

**Status:** done

- [x] The skill teaches every new manifest field, `apps.ask` option, result shape and error code. `AppsSkillTests` holds each name to the code.
- [x] The skill stays within its declared budget (raise the declaration deliberately if needed).
- [x] A Chromium case in the browser serving tests: a served page asks with a schema and an image, follows up, and gets an `AskError` on the cap.
- [x] `.claude/rules/apps.md` (and the channel and memory rules where they changed) states the new contracts.

## Comments

**Done (2026-11-26).**

- **The skill** (`Domain/Prompts/Skills/apps.md`): "Asking the assistant" now teaches what an ask is (a turn of
  the building agent for the viewer, paid by the household, in no chat, no memory beyond its conversation);
  `instructions` (16,000 bytes, written as a system prompt), `asks` (configured names, the shared keys listed,
  only calls never needing approval, a refused one told to the model), `model`; `apps.ask(text, {schema, files,
  conversation, signal})` and both answers; strict mode's rules (unchecked here, refused by the provider); images
  (types, 4 of 5 MB, a model that sees images); follow-ups within a day; the 2-minute wait; `AskError`'s fields
  and every code an ask is answered with, what to do for each, `envelope.freesAt` on `quota_exceeded` and the
  200-a-day cap an owner may change. Checking: `failedAsks` in report.json, a failed ask's entry (`reply`,
  `failure` on the preview), and that a check's asks are real, counted, within the check's 30 seconds, made once
  per viewport if on load, so tested from an assertion that awaits them with one `--viewport`. Stale lines
  fixed: an app "writes nothing" (it may, through its asks' tools), an app space's approval (servers too),
  `./publish`'s refusals (the instructions file), the errors file (kind and field `ask`, the library reporting
  failed asks, a member's reduced to the ask's code).
- **Budget**: body 1,840 -> **2,470** (measured 2,465), reason in the header. Description unchanged (96/110), no
  claim added, so the standing ratchet does not move. Only `Tests/Snapshots/skill.apps.md` changed.
- **`AppsSkillTests`** (8 new cases, all red against the old skill): every manifest field taught; the ask fields
  shown as JSON merged into the worked manifest parse valid; the shared servers named are `SharedServers` keys;
  `apps.ask`'s options are `AppAskRequest`'s plus the library's own (`signal`, `files`), the answers'
  keys `AppAskAnswer`'s, the schema `AppAskSchema`'s; `AskError`'s fields are what its class sets, `reply`,
  `failure` and `envelope.freesAt` the wire's constants; every code `AppAsks` spells (by reflection over its
  results, plus its refusal builders, `invalid_argument` and the library's `authentication`) is taught and every
  code taught is the taxonomy's; the limits stated are the shipped settings (`Asks:*`, `MaxInstructionsBytes`,
  `Checks:Deadline`); a failed ask's entry names `FailedAsk`'s fields.
- **Chromium** (`AppsServingInABrowserTests.APublishedApp_AsksItsAssistantThroughTheClientLibrary_AndShowsTheAnswer`):
  the household's cap set to 3 (the stand-in `app-asks` view), a fourth ask after the schema+image follow-up and the
  refusal is an `AskError` `quota_exceeded`, not retryable, with a parseable `envelope.freesAt`; both refusals
  are reported as kind `ask`. Passed on first run, as expected: the behaviour was 07/08's.
- **Rules**: `apps.md`'s skill bullet says what `AppsSkillTests` holds, limits included. Channel and memory rules
  unchanged (nothing in them moved in this ticket).
- **Not done / noted**: an agent has no way to see a household's own server's configured name (its tools carry
  the qualifier, its mounts a mount point), so the skill lists the shared keys and says "the household's own
  by its own name"; a model off the agent's `patchableModels` still surfaces only in the agent's log. No eval run.
