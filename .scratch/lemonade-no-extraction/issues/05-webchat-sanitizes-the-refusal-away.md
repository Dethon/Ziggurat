# 05 — WebChat swaps the person's Lemonade pick away before the refusal can fire

**What's wrong:** Ticket 02 makes an unoffered Lemonade model fail the turn loudly. In WebChat it
mostly never gets the chance, because the client already drops an unoffered model on the floor
before any turn is sent.

`AgentSettingsSelectors.Sanitize` (`WebChat.Client/State/AgentSettings/AgentSettingsSelectors.cs:37-49`)
re-checks each agent's stored model against every fresh catalogue push and substitutes
`agent.DefaultModel` when it is absent, with the comment *"a model the agent stopped offering
falls back to that agent's default rather than being sent on every turn for the server to
reject."* `AgentSettingsEffect` (`WebChat.Client/State/Effects/AgentSettingsEffect.cs:38-52`) runs
it on each `OnAgentsUpdated`.

Failure scenario — ADR 0042's *primary* one, the box going down:

1. `LemonadeModelDiscovery` fails closed and empties its list.
2. The catalogue fingerprint changes, agents re-register, `OnAgentsUpdated` reaches the browser.
3. `Sanitize` silently swaps the person's Lemonade selection to the agent's hosted default.
4. Their next turn carries **no lemonade patch at all**. It goes to a hosted provider, extraction
   runs on it, and there is no red bubble.

So ticket 02's throw only fires inside the race window between the box dying and the catalogue
reaching the browser. Both halves of the ADR fail past that window: *"never routing a local turn
to a hosted provider unannounced"*, and user story 5, *"an outage of my box does not become a
disclosure"*.

The spec's Out of Scope says "Any WebChat change. The lemon marker and the model list already
render from existing state" — written, it appears, without knowing `Sanitize` existed. The
server-side half of the change is correct and worth keeping; it is the client that quietly
undoes it.

**Note:** ticket 01's gate is unaffected. It reads the patch, and after a sanitize there is no
Lemonade patch to read — but there is also no local turn, because the turn genuinely went to a
hosted model. The gate is still correct; the person just did not get the box they asked for.

**Resolved:** keep sending it, and for every model rather than only Lemonade ones. `Sanitize` no
longer replaces a model at all — a pick the catalogue stopped listing is still the person's pick,
and the server is what says what happened to it. A stale reasoning effort still falls back, since
it is a fixed vocabulary the server warns and continues on and no host disappears underneath it.

**Status:** done

- [x] A person whose picked Lemonade model disappears keeps it, so the turn carries the patch and
      the server refuses it by name instead of answering from a hosted provider unannounced.
- [x] A drifted hosted pick is also kept — the decision was widened to every model, so nothing is
      ever swapped behind the person's back. The server still warns and answers on the agent's
      own, so ticket 02's scoping guard is untouched.
- [x] A client with nothing stored still takes the agent's default: no pick is not a stale pick.
- [x] `Sanitize_ALemonadeModel_IsValidWhileTheCatalogueListsIt`
      (`Tests/Unit/WebChat.Client/State/AgentSettingsSelectorsTests.cs:71`) stays green or is
      deliberately replaced.
- [x] ADR 0042's Consequences records the client half — with `Sanitize` fixed, the outage
      guarantee now holds past the catalogue reaching the client, which is what it always claimed.
