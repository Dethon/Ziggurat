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
  and every code an ask is answered with, what to do for each (retry a retryable one once or on a tap, never in a loop: a failed ask still counts), `envelope.freesAt` on `quota_exceeded` and the
  200-a-day cap an owner may change. Checking: `failedAsks` in report.json, a failed ask's entry (`reply`,
  `failure` on the preview), and that a check's asks are real, counted, within the check's 30 seconds, made once
  per viewport if on load, so tested from an assertion that awaits them with one `--viewport`. Stale lines
  fixed: an app "writes nothing" (it may, through its asks' tools), an app space's approval (servers too),
  `./publish`'s refusals (the instructions file), the errors file (kind and field `ask`, the library reporting
  failed asks, a member's reduced to the ask's code).
- **Budget**: body 1,840 -> **2,495** (measured 2,490), reason in the header. Description unchanged (96/110), no
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

**Review fixes (2026-11-26).** Each behaviour change red first, then green; no paid calls.

- **Images per conversation** (`bb982e07d`): `UploadStore.ReadBytesAsync(id, household, conversationId, ct)` hands a
  household's file only to a fetch whose call token names that household and whose turn (`call.Turn`) is the
  conversation it was saved under; Sugary's no-household files unchanged, laid out byte for byte as before. The
  agent's `ChannelAttachmentSource` cache is keyed by the turn's conversation too, else a second conversation of
  the same household would be answered from the first one's fetch without the server being asked. Tests:
  `AppsAsksTests` (same person, another conversation → nothing, over the wire), `HouseholdUploadsTests`,
  `ChannelAttachmentSourceTests.AFetch_IsNeverAnotherConversationsCacheHit`.
- **Caps unreadable for any reason** (`8b9e8c2c6`): `AskCaps.LimitAsync` answers `Unreadable`
  (`transient_dependency`) for anything but the accounts service refusing the proof (`permission_denied`) or the
  caller's own cancellation; a malformed view was a 500. Spec's "The cap" amended. Test: the `malformed` row of
  `CapsThatCannotBeRead_RefuseTheAsk_SendingAndCountingNothing`.
- **Fires on the apps channel** (`418f0a0ee`): the `NotAnAppTurn` refusal now runs before the fire path, so a fire
  sent there, with or without an app turn, is refused and never runs with the persona, tools and memory. Test:
  `ChatMonitorAppTurnTests.AFireOnTheAppsChannel_IsRefused_AndRunsNothing`.
- **No default bodies** (`f570b6383`): `IHouseholdConfiguration.AppAsksAsync` is abstract (test fakes that read no
  caps throw `NotSupportedException`). `IAttachmentSource` is now **one** method, the turn's: the id-only overload
  was used by no production caller, and keeping both abstract would have every stub implement two. Beyond the ask,
  deliberately. `UploadStore.ReadBytesAsync(id, ct)` is gone (tests read through the full overload).
- **Credential rule** (`0175cb9f4`): `Domain/Security/AppViewerCredential` is the one "assertion, or a check's call
  token, never both" rule, used by `ConversationGroup.PersonOfAsync` and `AppReader`; `CheckCallVerifier` puts a
  check's person on the `apps` channel for reads as well as asks (asks already restamped). The wire keeps its two
  nullable strings (`Assertion`, `CheckCallToken`): changing the notification's JSON shape would buy nothing the
  shared rule does not.
- **Prompt composition** (`4ae1b3f82`): `McpAgent.Compose(shape)` holds the name, language, day and zone once; the
  persona's and the app's prompts only say what differs. Snapshots unchanged.
- **Unwrap switches** (`6d925b6c0`): `AgentAsks` reads each step as `if (x is Refused) return …; var v = ((Valid)x)…`;
  the Domain result types are unchanged.
- **Published apps** (`686bb8bf7`): `Accounts/Admin/HouseholdPublishedApps` (read the listing, authors' names now)
  serves both pages; `HouseholdSpacesAdmin.PublishedAsync` is gone. **Skipped**: `PublishedApp`/`AdminSpaceApp`'s
  duplicated `Asks` + `Reach` — they are two wires (apps server → accounts, accounts → chat client) and folding one
  into the other changes the admin JSON for a two-line saving.
- **Literals** (`10453c586`): `RefusalAnswers.Codes` holds every refusal code (values unchanged: Sugary's app words
  them); `AppAsks.Refusal` switches on `Codes.Unchecked`. `agent-gone` is still known by its sentence's opening and
  closing (`IsAgentGone`, over the constants): `send_reply` carries no code and the sentence names the agent.
  `AppChecks.FailedAskAsync` reads the envelope through `ToolErrorResult.FromEnvelope` and `AppAsks.ReplyField`/
  `FailureField`, the asked text by `AppAskRequest.Text`'s wire name; the empty catch became `BodyAsync`, saying why
  an unreadable body is a failed request.
- **LINQ** (`686bb8bf7`, `d49e021e1`): `HouseholdSections.AppAsksProblems`, `AppErrorReport`/`AppErrorRead`'s
  problems, `HouseholdAppAsksAdmin.Summary`.
- **Comments** (`d49e021e1`): `AskCounts.TryCountAsync`'s and a registration line's "what" removed;
  `AppAskSchema.MaxNameLength` and `AppAsksSection.Apps` say why. The rest of the diff's one-line comments were read
  and kept (they state a why, or what a null or a constant means on a wire).
- **Rules** (`172fe7dab`): `channels.md`'s app-turn bullet and `apps.md`'s "App asks" trimmed to contracts and
  pointers (1,266 → ~820 words in apps.md), checked bullet by bullet against the old text; the new contracts
  (fires refused, images per conversation, `AppViewerCredential`) stated; `mcp-hosting.md` and
  `person-assertions.md` follow.
- **Spec** (`f0d543ceb`): an unapproved server is left out, not refused (error list and story 22); outposts are
  never offered to an ask; the preview's `failure` field is described.
- **Verified**: `dotnet build Ziggurat.sln` 0 warnings; full `dotnet test` 11,951 passed, 0 failed, 224 skipped (3 m 28 s).
