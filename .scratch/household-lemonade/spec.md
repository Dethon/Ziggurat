# A household's own Lemonade box through its tenant kit

Status: ready-for-agent

## Problem Statement

A household that runs its own Lemonade box can already offer that box's models to its own people. It does this by giving the box's address and key on its Keys page. For any household other than the home, that address has to be public https, because the deployment reaches the box over the internet.

Most households keep their box at home, behind a router, with no public address and no wish to open one. They already run the tenant kit, and the kit's servers reach the deployment through tunnels the household opens itself. But no kit service carries chat to a Lemonade box. So a household with a box on its LAN cannot use it, short of exposing the box to the internet.

## Solution

The tenant kit gains one more service, a **Lemonade forwarder**. It runs beside the household's box. It opens a tunnel to the deployment under the server name `lemonade`, like every other kit server, and passes whatever arrives on that tunnel to the box at its LAN address. The forwarder adds the box's key if the box needs one.

On its Keys page the household picks how its box is reached: **at an address** (as now) or **through the tenant kit**. Through the kit, the page asks for nothing more. The owner mints the `lemonade` credential on the Servers page as for any kit server, puts it and the box's LAN address in the kit's `.env`, and starts the forwarder.

The agent then reaches that household's box through its tunnel instead of by URL. Everything else works as for an address box. The box is asked for its models every minute. They are offered in that household's picker alone, and that household's turns on a Lemonade model run on it. With the tunnel closed, the box offers no models, as an unreachable address box does.

## User Stories

1. As a household owner with a Lemonade box on my home network, I want my household's people to chat with its models without giving the box a public address, so that I never open my network to the internet.
2. As a household owner, I want to choose on the Keys page whether my box is reached at an address or through the tenant kit, so that one form covers both setups.
3. As a household owner choosing the tenant kit, I want the page to ask me for no address and no key, so that nothing about my LAN leaves my house.
4. As a household owner, I want the box's key, if it has one, kept in my kit's `.env`, so that the deployment never holds it.
5. As a household owner, I want to mint the `lemonade` credential on the Servers page like any other kit server, so that setting it up works the way I already know.
6. As a household owner, I want the Keys page to show whether my box's tunnel is connected, so that I can tell a forwarder that is not running from a box with no models.
7. As a household owner, I want the Keys page to say that the connection state is unknown when the deployment cannot be asked, rather than "not connected", so that I don't chase a problem that is not mine.
8. As a household owner, I want the Servers page to list `lemonade` with its connection state like my other servers, so that I see all my kit in one place.
9. As a household owner, I want the `lemonade` server never offered as a tool server for my agents, so that I can't wire a chat box in as if it were tools.
10. As a household owner, I want to switch my box from an address to the tenant kit (and back) without removing it first, so that moving the box home is one save.
11. As a household owner, I want removing the box on the Keys page to stop its models being offered, whichever way it was reached, so that removal means the same thing in both setups.
12. As a household owner, I want revoking the `lemonade` credential to close the tunnel and stop the models being offered within a minute, so that I can cut the box off from the deployment's side.
13. As a household member, I want my household's box's models in my agent's model picker once the forwarder is connected, so that I can pick them like any other model.
14. As a household member, I want my turns on one of those models to stream their reply as they do on an address box, so that a tunnel makes no difference to me.
15. As a household member, I want a turn on my box's model to fail clearly when the tunnel is closed, as an unreachable address box does today, so that I know to pick another model.
16. As a household member, I want the box's models to disappear from my picker within a minute of the tunnel closing, and to come back within a minute of it reopening, so that the picker matches what will actually work.
17. As a household member, I want a subagent my agent spawns to run on the same box as its parent, so that delegated work stays on my household's hardware.
18. As a member of another household, I want never to see or reach someone else's box's models, through a tunnel or otherwise, so that one household's hardware is never another's.
19. As the home household, I want to be able to reach my own box through the tenant kit too, and have it take precedence over the deployment's configured box, as an address box of the home's already does.
20. As the home household with no box of my own, I want the deployment's configured box to keep serving my turns, so that nothing changes for me unless I opt in.
21. As a household owner, I want the forwarder to need nothing in the kit's `.env` but its tunnel credential, the box's LAN address and an optional key, so that setting it up is three lines.
22. As a household owner, I want the forwarder to run alone, without the rest of the kit, so that a household with only a Lemonade box can run only that.
23. As a household owner, I want the forwarder to refuse to start with no box address, saying so, so that a half-filled `.env` is caught at once.
24. As a household owner, I want the kit's README to list the forwarder with its server name, its environment and what it reaches, as for every kit server, so that I can set it up from the README alone.
25. As the deployment's operator, I want the forwarder to make no back-call, so that a `lemonade` credential reaches nothing of the deployment's.
26. As the deployment's operator, I want the relay to open only the forwarder's chat tag on a `lemonade` tunnel, and the forwarder to take no other, so that the tunnel carries chat and nothing else.
27. As the deployment's operator, I want the tunnel's `lemonade` server name kept apart from voice's `lemonade` back-call tag, so that the two never get confused in the rules or in the code.
28. As the deployment's operator, I want a household's box to be reached only through that household's own tunnel, never one of the same name from another household, so that isolation holds by construction.
29. As the deployment's operator, I want the tunnel box's state in the agent's household configuration and in the `lemonade-boxes` view with no key in it, so that nothing secret travels for it.
30. As the deployment's operator, I want a household suspended to have its `lemonade` tunnel closed with the rest, so that suspension stops its box being used like everything else.
31. As a household owner, I want the Keys page's admin log to record when the box changes how it is reached, so that the household's audit trail shows it.
32. As a household owner, I want two owners' concurrent saves of the box to follow the Keys page's existing rules, so that the tunnel option brings no new conflict behaviour.

## Implementation Decisions

- **The keys section's box has two shapes.** It is reached either at an address (`apiUrl` and an optional `apiKey`, as now) or through the tenant kit (`throughKit: true`, nothing else). Exactly one of the two. The keys section's problems check refuses both or neither. The address rule (public https for any household but the home) applies to the address shape alone.
- **The box's key for a kit box lives in the kit.** The forwarder adds the bearer header from its own environment when one is set. The household's keys section holds no key for it, and the agent sends none down the tunnel.
- **The admin API's Lemonade box route takes either shape.** A kit box is saved without a probe: there is no address to ask. Its status on the Keys page is the tunnel's state, read through the accounts service's existing connected-servers client (the agent's own listing): connected, not connected, or unknown when the agent cannot be asked. An address box keeps its probe and its key status. The status record gains the kit shape: no address, and a connection state instead of a key status.
- **The Keys page's Lemonade slot offers the choice.** At an address shows the existing address and key fields. Through the tenant kit shows a line on what to set up (mint `lemonade` on the Servers page, then three `.env` lines) and the connection state. Saving one shape over the other replaces it. Removing works as now.
- **The agent's views carry the shape.** The agent view's `lemonade` and the `lemonade-boxes` view carry the box as saved. For a kit box that is the `throughKit` flag and nothing else.
- **The agent's per-household hosts reach a kit box through the relay.** For a kit box, `HouseholdLemonadeHosts` builds a host with a fixed synthetic base address, plus a transport whose every connection is a new stream of that household's open `lemonade` tunnel under the forwarder's chat tag. The tunnel is looked up per connection by household and server name, never by name alone, so a reopened tunnel is used at once and a closed one fails the connection. That one transport serves both the box's model discovery and the turns' Lemonade chat client (which already takes a transport handler). A closed tunnel makes discovery fail closed, so no models are offered, and makes a turn fail as an unreachable address box's does. A box whose shape changes gets a fresh discovery, as an address change does today.
- **The relay exposes a per-household stream transport.** The relay already forwards one exchange down a tunnel to a tagged listener for the browse pages. This feature adds a reusable way to get an HTTP transport onto a given household's server and tag, built on the same tunnel streams, for code in the agent that makes requests rather than relaying them.
- **The server name is `lemonade` and the forwarder's listener tag is `chat`.** `lemonade` joins the household server names the deployment itself finds. The relay-opened tag is `chat`, so that it never reads as voice's `lemonade` back-call tag, which is a different thing (the deployment's own speech box).
- **The forwarder is a kit service of its own**, not a tag on an existing service. That keeps the kit's "one name, one server" shape and lets a household run it alone. It is a small .NET host on the hosting library's tunnel client, with no MCP tools and one tunnel listener under the `chat` tag. It reverse-proxies every request it gets to the box's API base, which is set in its environment (the same form the address shape takes, ending in the API path), with responses streamed unbuffered, since Lemonade answers the Responses API as server-sent events. It takes no absolute URI and no other host: its one destination is the configured box. Its environment is the kit's usual tunnel settings, the box's API base, and an optional box key.
- **The forwarder makes no back-call and publishes no metrics.** The tunnel rules' back-call table and per-server lines say so, as they do for every kit server.
- **The `lemonade` server is not a tool server.** The relay never dials it as MCP. The Servers page lists it with its connection state like any own server but never offers it to an agent template, and the agents section's checks gain a refusal for an agent configuration naming it as a server.
- **The kit's compose file, `.env` template and README gain the forwarder**: its service, its credential variable, the box address and the optional key, plus its row in the README's server table.
- **The rules docs follow the code**: the household configuration rules (keys section and views), the tunnel rules (server name, tag, no back-call), the admin rules (the Keys page's slot), and the routing rules' "A Lemonade box is its household's".

## Testing Decisions

- A good test drives the feature from outside, the way a household or the agent sees it. A box's models show up in the right household's hosts. A chat request reaches the box and streams back. The Keys page's admin API saves, refuses and reports states. Tests never assert on internal collaborators, and the transport is never faked where the real tunnel can run.
- **Main seam: the relay integration test.** It uses the existing tunnel test hosts (a relay host, a tunnelled server, the accounts server), as the browse pages test does. The real forwarder runs tunnelled to the relay under a household's `lemonade` credential, with a stub Lemonade behind it (a small Kestrel answering models and a streamed Responses call). It asserts:
  - the household's hosts list the stub's models, and another household's don't;
  - a chat call through the household's host streams the stub's reply chunk by chunk;
  - the forwarder adds the configured key and the agent sends none;
  - closing the tunnel empties the models on the next refresh and fails a chat call; reopening restores them;
  - a second household with a `lemonade` tunnel of its own never reaches the first's box;
  - the forwarder accepts no stream under any other tag.
- **Accounts: the keys admin tests** (the existing seam). They cover saving a kit box without a probe, refusing both shapes or neither, replacing one shape with the other, removal, the connection state from a faked agent listing (connected, not connected, unknown), and the admin log line.
- **Kit: the tenant kit conformance tests** (the existing seam). The forwarder service is tunnelled by the kit's environment variables under the `lemonade` name, publishes no port, makes no back-call, and appears in the README's table.
- **Agent configuration**: the agents section's checks, with their new refusal of `lemonade` as a tool server. The existing household Lemonade hosts unit tests gain the kit shape where they already cover shape changes and removal.
- **Smoke**: a smoke test on the live stack, like the household vault one. A household saves a kit box, mints its `lemonade` credential and starts a forwarder in the smoke kit, pointed at the smoke key stub's Lemonade models answer. Its agent's picker then offers that model and the home's picker does not. It is started by its own smoke-stack verb, as the household vault is.
- Prior art: the browse pages tunnel tests and their relay test hosts, the keys admin tests, the household Lemonade hosts tests, the tenant kit conformance tests, and the household vault smoke test.

## Out of Scope

- The deployment's own Lemonade for speech and embeddings (voice's `lemonade` back-call) stays the deployment's.
- Speech, transcription or embeddings on a household's own box. This feature carries chat and model discovery only.
- Probing the box through the tunnel when it is saved. The connection state and the models in the picker within a minute are the check.
- Tracking a household box's usage in the admin usage page's Lemonade line. That line stays about the shared box.
- Any change to the address shape, its probe or its address rule.

## Further Notes

- The address shape was built and merged into web-identity on 2026-11-04 (household-lemonade). This spec is the second of the two ways in the user asked for.
- The user decided that a Lemonade box's models are offered and run only for the household that provides it. The tunnel shape keeps that by construction: a household's box is reachable only through its own tunnel.
- The user settled the earlier draft's open questions on 2026-11-04: the forwarder is a service of its own; the key lives in the kit's `.env`; the box is saved unprobed, with the tunnel's state as its status; it is named `lemonade`, with a listener tag `chat`. No back-call was the one safe answer to the fifth.
- Discovery already empties a box's models when a listing fails, so a closed tunnel removes them with no new code.
