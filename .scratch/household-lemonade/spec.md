# A household's own Lemonade box through its tunnel

Status: needs-triage

## Where this stands

Built on branch `household-lemonade` (2026-11-04): a household's own Lemonade box is set on its Keys
page as an **address and key**, probed by its model listing, held to a public https address for any
household but the home, read by the agent from the `lemonade-boxes` view, asked for its models every
minute, offered in that household's picker alone and used for its turns alone
(`.claude/rules/openrouter-routing.md`, "A Lemonade box is its household's").

What that leaves out is a box **behind a home router**: a household's Lemonade server on its own LAN,
with no public address. The user wants both ways in (2026-11-04). This spec is the second.

## The idea

The household runs one more tenant-kit service that opens a tunnel to the deployment under the server
name `lemonade` and forwards what arrives to its box on the LAN. The agent then reaches the box
**through that tunnel** instead of by URL, with everything else — discovery, the whitelist, the routing
client, the picker — as built.

- **Kit side**: a small forwarder service (`lemonade` in `TenantKit/docker-compose.yml`), tunnelled by
  its environment like every kit server (`MCP__TUNNEL__HUB`, `TUNNEL_CREDENTIAL_LEMONADE`,
  `MCP__TUNNEL__SERVERNAME=lemonade`), with `LEMONADE_URL` — the box's LAN address — and nothing else.
  It registers one tagged listener (`lemonade`) that reverse-proxies to `LEMONADE_URL`, streamed
  (Lemonade answers the Responses API as server-sent events).
- **Relay side**: `TunnelForwarder` already forwards an HTTP exchange down a household's tunnel to a
  tagged listener (browse pages). The agent's Lemonade client for such a household gets an
  `HttpMessageInvoker` over `OpenTunnel.Forwarding("lemonade")` instead of the shared pool, at a
  synthetic base address (`http://lemonade.<household>.tunnel/api/v1`).
- **Configuration**: the keys section's box takes either an `apiUrl` or `viaTunnel: true` (one or the
  other). The Keys page offers "Reached through the tenant kit" beside the address; the probe then asks
  through the relay (the accounts service asks the agent's relay, as the household list does) or saves
  it unchecked when the tunnel is not open.
- **Offline**: with the tunnel closed the box offers no models (discovery fails closed, as a URL box's
  does); the Servers page lists `lemonade` like any own server.

## Decisions to make before the tickets

1. **Forwarder image**: a new tiny .NET host on `Mcp.Hosting`'s tunnel client (no MCP tools, one tagged
   listener), or a tag on an existing kit service (web search already registers a pages listener)?
   A service of its own keeps the kit's "one name, one server" rule and lets a household run it alone.
2. **Who holds the key**: the box's key in the household's keys section (sent by the agent through the
   tunnel, as now), or in the kit's `.env` (the forwarder adds it, so it never leaves the house)?
3. **Probe through the relay**: is "saved unchecked until the tunnel opens" enough, or should the
   Keys page wait for the tunnel and probe through it?
4. **Back-call scope**: the forwarder makes no back-call and the relay opens no stream from it but the
   `lemonade` tag; the tunnel rules' per-name back-call table gains a row saying so.
5. **Name**: `lemonade` as the server name (a kit name, so `TenantKitConformanceTests` and
   `HouseholdServerNames` list it) — or `lemonade-chat`, since the deployment's own Lemonade (speech,
   embeddings) is a different service the kit's voice back-calls already call `lemonade`?

## Out of scope

The deployment's Lemonade for speech and embeddings (`backCalls.lemonadeUrl`) stays the deployment's.
