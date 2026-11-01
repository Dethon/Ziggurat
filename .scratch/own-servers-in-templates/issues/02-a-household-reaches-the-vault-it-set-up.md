# 02: A household's agents reach the vault it set up

**What to build:** The tracer bullet. The accounts service's view of a household for the agent
carries the household's set-up own server names (server names with a live tunnel credential; outposts
never count). The agent withholds every own server name the household has not set up from the
household's agent definitions and from the workers they spawn, so such a server is never dialled and
never named offline; the home household withholds none. The barebones Jonas template names `vault`.
An owner who added a vault on the Servers page and runs it at home finds their agent reading their
notes; an owner who never added one never hears of a vault; a vault that was set up but is off is
named offline on the turn, and is back on the turn after it reconnects. Spec: `../spec.md`.

**Blocked by:** 01 (a withheld server may be an own server)

**Status:** ready-for-agent

- [ ] The accounts service's agent view lists the names of the household's live tunnel credentials, not revoked ones and never an outpost's (household configuration tests)
- [ ] A household with `vault` set up and connected: its Jonas-template agent reads the vault (turn-level, multi-agent factory household tests over an in-process vault)
- [ ] Set up and not connected: the turn names `vault` on its offline line
- [ ] Not set up: no vault tools, no mount, no offline line
- [ ] A credential revoked since the last turn: the next turn has no vault and no offline line
- [ ] A worker spawned by a household that did not set up the vault does not reach it and names nothing offline
- [ ] The home household's seeded agents reach and name their own servers exactly as before
- [ ] An agent view without the field (an older accounts service) reads as nothing set up
- [ ] The template's prompt snapshot regenerates
