# 04: Follow-ups

Spec: `.scratch/app-asks/spec.md` ("Conversations").

**What to build:** Every answer carries a conversation id. Passing it on the next ask continues that
conversation with its history, for the same app and viewer only, within a follow-up window.

**Blocked by:** 02

**Status:** done

- [x] The first ask mints the id and its answer returns it. A follow-up is a turn on the same conversation, and the model sees the earlier exchange (origin and agent seams).
- [x] A follow-up from another app, another viewer, or an id never minted is refused before any turn, with the unknown-or-expired code.
- [x] A follow-up after the window, or after an apps-server restart, is refused the same way, so the app starts afresh.
- [x] The window is an apps-server setting.

## Comments

**Done (2026-11-26).** What 06-11 build on:

- **The wire** (`AppAskRequest`): `{"text", "schema"?, "conversation"?}`, still strict. `conversation` is read as
  written (any string, `null` = none; a non-string is 400); its shape is never judged, so a malformed id is
  answered as an unminted one. Answers unchanged: a continued conversation's 200 names the same id.
- **The refusal**: **410 `conversation_expired`** (`ToolError.Codes.ConversationExpired`, not retryable,
  `AppAsks.ConversationExpired`, hint: ask again without it). One answer for an id never minted, another app's,
  another viewer's, past `Asks:FollowUpWindow` after its last turn, after an apps-server restart, evicted at the
  ceiling, or **opened on a building agent the app no longer has** (the agent keeps history per conversation
  and agent; `BuildingAgent` is never reassigned today, so that case is pinned on `AskConversationsTests`
  alone). Checked last before the turn (after body, instructions), since continuing restarts the window.
  The client library needed no change: `apps.ask(text, { conversation })` already sends it and the
  envelope becomes `AskError`.
- **The bound**: `OpenConversations` now takes a `ceiling` (Sugary's `OpenEstimates` and `AskConversations`
  both pass 10,000) and holds one lock over open and continue, so it is exact. Closed entries go on every open
  (and a closed one found on continue); at the ceiling the one longest without a turn is forgotten, never a
  new conversation refused.
- **Schema and servers**: each ask carries its own `schema` (a follow-up without one is a text answer; the
  agent reads the format off the turn's own message) and its own manifest's `asks`, as before.
- **Tests**: `OpenConversationsTests` (the ceiling), `AskConversationsTests` (owner, changed agent, the
  window is the setting), `AppAsksTests` (conversation read as written, the code), `ToolErrorTaxonomyTests`
  (by reflection), `AppsAsksTests` (a follow-up on the same conversation and agent, schema then text; never
  opened / another app's / another viewer's refused 410 before any turn; past the window on a fake clock
  handed to `AskConversations` alone; a restarted server over a copy of the store),
  `AppTurnAgentTests.AFollowUp_SeesTheEarlierExchange_AndCarriesItsOwnFormatOrNone` (a real
  `RedisThreadStateStore` for that case alone: the fixture's mock store keeps no history, which the other
  cases' one-request-per-text assertions rely on; passed on first run, red with the mock).
- **The ceiling is deployment-wide**: until 07's cap, one app asking in a loop can push other households'
  conversations out (they then get `conversation_expired` and start afresh); 07 should bound that.
- **Left / noted**: published and preview are one app id, so the author can continue a published
  conversation from the preview under the tree's instructions (within "same app, same viewer"). Two
  simultaneous asks on one conversation are possible (Sugary has the same exposure). Images rehydrating on a
  follow-up is 06's. The `apps` skill does not teach `conversation` yet (11).
