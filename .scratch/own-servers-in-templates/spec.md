# A household's agents reach the own servers it set up

Status: ready-for-agent

## Problem

Found in the manual test of 2026-11-02 (web-identity, smoke stack). An owner of a household other
than the home mints a credential for `vault` on the Servers page, starts the kit's vault with it, and
the page shows it connected with its tools. Her agent never uses it: the barebones `jonas` template
(`Domain/Prompts/Agents/jonas.md`) names only the shared servers, owners may change nothing but an
agent's name and instructions (`HouseholdAgents`), and so no agent of hers names `vault`. Outposts
are the only own servers her agents reach (`uses-outposts: true`). Story 18 of the admin spec
("add one of my household's own servers ... so that I can start it at home") promises the opposite.

The template names none on purpose: a server a definition names that is not connected is listed on
every turn as offline (`OwnServerEndpoints`, `TurnDecoration.OfflineLine`), and a household that never
set one up should not be told daily that its vault is off.

## Decision (2026-11-02)

The template names the kit's servers, and a household's agent dials **only those the household has set
up**: a server name with a live (not revoked) tunnel credential minted for that household. One the
household never set up is left out silently — no offline line — while one it set up that is not
connected right now is offline, exactly as for the home today.

## Behaviour

- `jonas.md` names `vault`, `sandbox`, `websearch`, `printer`, `library` beside its shared servers,
  with the whitelist and features they need (compare the home's seeded `jonas` in
  `HouseholdMigration/Home/agents.json`); `homeassistant` and `voice` stay out (home-only until their
  turn).
- The accounts service's agent view (`AgentHouseholdConfig`) carries the household's set-up server
  names (`OwnServers`: server names with a live credential, outposts excluded), read where
  `TunnelCredentialStore` lists them; revoking the last credential for a name removes it, and minting
  announces a change so the agent's cached view drops (`HouseholdConfigProtocol` already announces saves).
- The agent's own-server resolution (`OwnServerEndpoints.Resolve`, fed by the spec's household) leaves
  out a named own server the household has not set up, without naming it offline. The home household
  keeps today's rule (every named server it does not have connected is offline), since its seeded
  agents name what its kit runs.
- A worker inherits the parent's household, so it follows the same rule with nothing new.

## Tests

- Projection/resolution: a household with `vault` set up and not connected → `vault` offline; with
  none set up → no `vault`, no offline line; the home → offline as today.
- Accounts: the agent view lists live credentials' names, not revoked ones, not outposts.
- Smoke: Lucía-style household mints `vault`, starts a kit vault, her agent reads a note through it
  (the step the 2026-11-02 manual test could not complete).
- Snapshots for the template's prompt (`jonas` template) regenerate; run the changed eval tier for the
  template if its prompt sections change.

## Out of scope

Home Assistant and voice for non-home households; letting owners choose servers per agent.
