---
paths:
  - "Infrastructure/Tunnels/TunnelForwarder.cs"
  - "Infrastructure/Tunnels/OpenTunnel.cs"
  - "Infrastructure/Tunnels/BrowsePages.cs"
  - "Infrastructure/Tunnels/RelayedCalls.cs"
  - "Infrastructure/Clients/Relay/**"
  - "Infrastructure/Clients/Voice/VoiceHubHttp.cs"
  - "Mcp.Hosting/Tunnel/RelayCredential.cs"
  - "Agent/App/BrowsePagesApi.cs"
  - "Agent/App/RelayedCallsApi.cs"
  - "Domain/DTOs/Tunnel/BrowsePagePath.cs"
  - "Domain/DTOs/Tunnel/BrowsePagePolicy.cs"
  - "Domain/DTOs/Tunnel/RelayedCallProtocol.cs"
  - "DockerCompose/docker-compose.yml"
  - "DockerCompose/caddy/Caddyfile"
  - "Tests/Integration/Tunnels/BrowsePagesTests.cs"
  - "Tests/Integration/Tunnels/RelayedCallsTests.cs"
  - "Tests/Integration/Tunnels/TunnelledVoiceHub.cs"
---

# Forwarding into a household's tunnel

See also: `tunnel.md` (wire, registry, `RelayHandler`), `own-servers.md`, `tenant-kit.md`.

## Exchanges the relay forwards to a household's server

Some requests reach the agent for a household's server rather than from a turn: a browse page a
person opens, a shared server's call. **`TunnelForwarder`** (Infrastructure/Tunnels) forwards one HTTP
exchange down the server's tunnel to the listener a tag names, and its answer back — an upgraded
WebSocket included, whose bytes then run both ways until either end closes.

- It is **YARP's `IHttpForwarder`** (`AddHttpForwarder()` in `AddTunnelRelay`) over
  `OpenTunnel.Forwarding(tag)`: an `HttpMessageInvoker` whose `ConnectCallback` opens a stream with
  that tag, one pool per tag per opening, configured as a reverse proxy's must be — `UseCookies =
  false` above all, or the pool would send one person's claim cookie with the next person's request.
  Why not a raw pipe: hop-by-hop headers, streamed bodies, a client gone mid-response and a 101 handed
  to a duplex stream are what the library is, and the live view needs all of it.
- The request goes on **as it came**: the base `HttpTransformer` (not `HttpTransformer.Default`, which
  adds X-Forwarded-* of its own and drops Host) copies Host and the proxy's `X-Forwarded-Proto` — the
  live view's origin check compares `Origin` with Host, and its cookie is `Secure` by
  `X-Forwarded-Proto`. Only the path is the caller's to choose, and the headers it withholds.
- The `mcp` tag's pool presents the tunnel credential and sends rotated keys first (`RelayHandler`);
  any other tag's presents nothing — a browser's request is not the agent's to vouch for.
- `ActivityTimeout` is 30 minutes, the longest a live view lasts (a still page sends nothing and is
  not dead). The tunnel's or browser's end still ends it at once.
- A forward that fails before anything was sent (tunnel closed under it) is answered by the caller as
  the server being offline.

### Browse pages

The proxy routes `/browse/*` on the main host to the agent (`BrowsePagesApi`, `BrowsePages.Path`),
never to the web search container (`DeploymentConformanceTests`: no route names it). A link is
`/browse/<household>/<page>/<token>` (`BrowsePagePath`, Domain, the one spelling both ends read);
`BrowsePages` forwards every exchange under it **unchanged, path included,** to that household's web
search under the `pages` tag (`TunnelProtocol.PagesTag`, registered via `AddTunnelListener`), landing
on the pages listener so `PagesListener`'s split holds. The link, claim, cookie and live view are the
server's: served under `/browse/{household}` (`HandoffEndpoints`), both view cookies scoped to
`Path=/browse/<household>`, that base rendered into the page so every request (claim, Done, the live
view's socket, logins list, Forget) carries the household. With that household's tunnel not open the
agent answers **502 with a page of its own** saying the household's browser is offline.

**The answer is not trusted on the main origin.** A household's web search runs on its own machine,
and under `/browse` its page is same-origin with the chat client and admin API, so a swapped server's
script would act as whoever opened its link. Via `TunnelForwarder`'s `answered` hook, before a byte is
sent, `BrowsePages`:

- replaces the server's CSP with `BrowsePagePolicy.ContentSecurityPolicy` — only the handoff and logins
  scripts run, pinned by hash; no other script, plugin, form, frame or base;
- drops `Clear-Site-Data`;
- keeps a `Set-Cookie` only if it is one of the two view cookies on exactly `Path=/browse/<household>`
  with no `Domain`.

The scripts live in `Domain/DTOs/Tunnel/BrowsePagePolicy.cs` for both ends, with LF line ends (a
browser hashes what its parser read), reading per-link values from a JSON data element so their text
is the same for every link. A kit with different scripts shows its page without them until updated;
when they change, the last release's hashes stay pinned for one release (`BrowsePagesTests`, in a real
browser too; smoke checks the header through Caddy).

**Nor is the server sent what the browser holds for the origin.** A link's request carries every
`Path=/` cookie — the opener's session, the operator's when a household sends one its link — so
`BrowsePages` forwards `Cookie` with the two view cookies alone and no `Authorization`, for every
browse exchange, the home's included (`BrowsePagesTests`).

### A shared server's calls to a household's server

A shared deployment server reaches a household's own server through the relay's internal endpoint,
`/internal/relay/<server name>/<the server's own path>` (`RelayedCallProtocol`, `RelayedCallsApi`,
`RelayedCalls`), **never the network**; the proxy routes nothing under `/internal`
(`DeploymentConformanceTests`). Callers: the timers server (ring, dismiss, validate satellites on the
household's voice server, `.claude/rules/timers.md`) and Sugary (glucose alarm); both wire the hub's
client with `AddHouseholdVoices` (`Infrastructure/Clients/Voice`); compose points their
`VOICEHUB__BASEURL` at `http://agent:8080/internal/relay/voice/`.

- **The caller is checked against what the agent already holds**: it names itself
  (`X-Ziggurat-Caller`, its own `Mcp:Audience`) and presents the credential its own `/mcp` asks for —
  exactly the agent's `MCP__SECRETS__<name>` (`McpCredentials`). No secret exists for this endpoint,
  and one server's credential opens only what that server may reach. Client end:
  `RelayedCallCredential`, added when the base address is under the relay's path
  (`RelayedCallProtocol.IsRelayed`); a directly dialled server is presented neither.
- **What it may reach is a list in `RelayedCalls`**, by caller, household server and path prefix
  (`mcp-timers` and `mcp-sugary` → `voice` under `/api/voice`), checked on the Kestrel-normalised path.
  Missing/wrong credential 401, off the list 403; both logged and published (`ErrorEvent`,
  `RelayedCallRefused`), neither forwards a byte.
- **Every request names its household** (`X-Ziggurat-Household`, `RelayedCallProtocol.HouseholdHeader`)
  and reaches that household's server alone (`TunnelForwarder.ForwardAsync` over `TunnelRegistry.Find`)
  — never another's of the same name; naming none is 403, never sent to the home's. A listed caller is
  a shared server serving every household, so it may act for any; it names the household from what it
  holds (the timers server: the household the arming call named). The relay believes it on the
  caller's own credential; an agent-signed proof (the timer write's delegation) would be stronger and
  is not built.
- **Forwarded on the `mcp` tag** (pool presents the household server's tunnel credential,
  `RelayHandler`), withholding the caller's name, the household header and the deployment's
  `X-Announce-Token`: nothing of the deployment's reaches a household. A tunnelled server's non-`/mcp`
  endpoints recognise the relay by `RelayCredential` (registered by `AddTunnel` while tunnelled):
  voice's `VoiceHubAuth` lets it through, so the kit's voice never holds the deployment's announce
  token.
- **Offline is a gateway's answer**: the household's tunnel not open, or closing under the call, is
  **502 with the reason** in the body. `VoiceHubHttp` reads 502/504 as the hub unreachable
  (`VoiceHubUnavailableException`), so a timer's create fails `TransientDependency` ("the voice hub
  is unreachable, so the timer was not armed") and its fire is logged as not rung (timers.md).

### Which household

A shared server's call names it (above). **A browse link names it too**: web search mints the link
where `web_login`/`web_logins` is called, under the household of the account the call token names
(`ILoginHandoffs.BeginAsync`, `ILoginsPages.MintAsync` take the `Account`) — the server's own
household, since a turn reaches only its household's web search. The relay forwards to that
household's tunnel alone (`TunnelForwarder.ForwardAsync`); none open is the offline page, and another
household's web search — the home's included — is never a fallback. View cookies on
`Path=/browse/<household>` mean a browser holding two households' views sends each server its own key
alone.

**A link naming no household** — first segment after `/browse` a page (`handoff`, `logins`), which no
household id (32 lowercase hex) can be — came from a web search older than household links, when every
link was the home's, and still goes to the home's (`TunnelForwarder.ForwardHomeAsync`) so an unrebuilt
kit keeps working; a rebuilt server serves nothing there. Tested in `BrowsePagesTests`.
