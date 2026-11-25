# 06: App read API and client library

Spec: `.scratch/app-spaces/spec.md` ("The app read API (the agent)", "Serving built apps").

**What to build:** A built app reads the household's data as its viewer. The served client library offers `apps.read`, `apps.glob`, `apps.info`, `apps.search`, `apps.query` and blob reads on virtual paths, with the tools' result shapes. These go to `/_vfs/*` on the app's origin. The apps server authenticates the viewer by the app session and forwards the read to a new internal agent route. It sends both the agent API's secret, which carries the app's id, building agent and `reads`, and the viewer's current `apps` assertion, which carries the person and household. The agent answers through the `VfsCall` meaning layer, over a read registry for (household, building agent, viewer) that is cached briefly.

**Blocked by:** 04, 05

**Status:** ready-for-agent

- [ ] Two viewers of one app each get their own `/sugary` rows.
- [ ] A read outside the manifest's `reads`, any operation that changes anything, and a read the building agent's whitelist would ask approval for each get the tools' permission envelope.
- [ ] A request without the secret, or without a believable `apps` assertion, is refused. The agent believes no other header.
- [ ] A withheld feature or an offline own server answers as a turn would, with the tools' unavailable envelope for offline.
- [ ] A building agent the household no longer has refuses every read and names the agent.
- [ ] The bridge's per-file byte ceiling applies.
- [ ] The registry cache is rebuilt when the household's servers change.
- [ ] Over-the-wire tests follow `VfsBridgeOverTheWireTests`, against in-process MCP servers.
