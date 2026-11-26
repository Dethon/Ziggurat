# 05: Tools for asks

Spec: `.scratch/app-asks/spec.md` ("The app manifest gains three fields"; "What the agent does with an app turn").

**What to build:** The manifest's `asks` names servers. An app turn's session is built from the
building agent's servers that are in that list: their tools, mounts, prompt sections and skills, and
no others. Any call whose approval would be asked is refused at once with the usual rejection, and
nothing is sent to the anchor or offered on any channel. Sources of approval: the whitelist, the exec
screen, Jev, and as-person browsing.

**Blocked by:** 02

**Status:** ready-for-agent

- [x] `AppManifest` takes `asks`: server names, shape-checked (origin seam).
- [x] The model is offered only the declared servers' tools, and the file tools see only their mounts (agent seam, FakeOpenRouter).
- [x] A server declared but not the building agent's is simply absent.
- [x] An empty or absent `asks` means no tools.
- [x] A whitelisted-ask call, an exec-screen ask and an as-person ask are each refused at once. No `request_approval` and no `offer_approval` is sent (agent seam).
- [x] A call that never asks runs, acting as the viewer (agent seam).

## Comments

**Done (2026-11-26).** What 06-11 build on:

- **A server's name in `asks`** is the one its tools carry (`mcp__<name>__…`): `McpServerEndpoint.Qualifier`
  (a household server's own name, resolved or not; a URL's `host`, or `host-port` off the scheme's default
  port, e.g. `mcp-sugary-8080`). `AppManifest.Asks` shape-checks with `ServerAddress.IsQualifier` (dotted DNS
  labels) and refuses duplicates; `McpServerEndpointTests` pins every endpoint's name to pass that check.
- **The apps server** sends the origin's own manifest's `asks` as `AppTurn.Servers` (served commit's /
  tree's). **A shared app's asks are granted none** (`AppPages.Granted` sets `Asks = []`): fail-closed until
  09 gives `AppReach` an `asks` and narrows `Within` it.
- **Narrowing** (`McpAgent.AppSessionAsync`, `SessionReach.NarrowedTo`): the definition's endpoints whose
  qualifier is declared, filtered before own-server resolution, never outposts, no domain tools (no workers,
  no memory). File tools see only those mounts; the prompt gains their mount/prompt sections and skills
  (`BuildAppInstructions`). Skills are offered (list + `load_skill`) but **never preloaded** (the
  `SkillsProvider` skips the judge for an app turn; the group already started none).
- **Session lifetime**: `McpAgent._threadSessions` is keyed by the group's `AgentSession` and lives as long
  as the agent (one conversation x agent), reused while `ServerReach.ReachesTheSameAs`. That compared only
  own-server states and outposts, so `ServerReach.NarrowedTo` (the declared names in definition order) joins
  the comparison: an ask declaring other servers rebuilds; one declaring none of the agent's disposes the
  thread's session and runs in none (its skills and mounts are read off the thread). Still no warmup for a
  group an ask opened.
- **Approvals**: one choke point, `ToolApprovalChatClient.RequestAsync`, refuses when the turn's options carry
  `ToolApprovalChatClient.AsksNobodyKey` (set by `McpAgent` for an app turn). The auto-approved notify (a
  `request_approval` in `Notify` mode) is suppressed too. A refused call ends the turn as any rejection does,
  with no text, so the origin answers it 422 `transient_dependency` (no answer); 10 may want a code of its own.
  The exec screen still asks Jev before refusing, since its verdict is what decides.
- **Tests**: `AppTurnAgentTests` (three asks on one conversation shrinking to none; a whitelist ask, an
  as-person ask and an exec-screen ask refused with no ask/offer/notify, beside an ordinary turn asking all
  three; a never-asking call signed as the viewer), `AppsAsksTests` (published vs preview `asks`, a shared app
  granted none), `AppManifestTests`, `McpServerEndpointTests`.

