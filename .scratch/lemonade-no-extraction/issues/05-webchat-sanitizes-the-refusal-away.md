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

**Blocked by:** none. Needs a decision on what WebChat should do when the model a person picked
has gone away: keep sending it so the server can refuse it loudly, or tell them in the client.

**Status:** needs-triage

- [ ] A person whose picked Lemonade model disappears is told, rather than silently moved to a
      hosted default.
- [ ] `Sanitize`'s existing behaviour for hosted models is unchanged — a drifted hosted pick
      still falls back quietly, matching ticket 02's scoping guard.
- [ ] `Sanitize_ALemonadeModel_IsValidWhileTheCatalogueListsIt`
      (`Tests/Unit/WebChat.Client/State/AgentSettingsSelectorsTests.cs:71`) stays green or is
      deliberately replaced.
- [ ] ADR 0042 is amended: as shipped, its outage guarantee holds only until the catalogue
      reaches the client.
