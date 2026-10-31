# 08: Back-calls from a household's server

**What to build:** A tunnelled server reaches back to the deployment only over its tunnel and only to an allowlist the relay enforces: the accounts service's resolution and guest endpoints (voice), the agent's VFS bridge (sandbox), the metrics feed, the shared Lemonade, and the topic-minting API. Each back-call carries the tunnel's identity; anything else is refused. Spec: `../spec.md` ("Back-calls").

**Blocked by:** 03

**Status:** done

- [x] Each allowlisted back-call works through the tunnel and says which household's server asked
- [x] A back-call to anything else is refused and reported
- [x] A sandbox command reads and writes another mount through the bridge over the tunnel (`SandboxOverTunnelE2ETests`, the real image)

Notes (implementation):

- Server side: a loopback port per back-call (`MCP__TUNNEL__BACKCALLS__<tag>`); clients change only their address (`ACCOUNTS__BASEURL`, `AGENT__BASEURL`, `STT__OPENAI__BASEURL`, `TTS__OPENAI__BASEURL`, `METRICS__URL`, `VFSBRIDGEURL`). The kit (11) sets them.
- Relay side: `BackCallGateway`, a Kestrel of its own fed by the back-call streams; the allowlist is `BackCallTargets`; identity headers `X-Ziggurat-Back-Call-Household`/`-Server`; refusals are `BackCallRefused` on the errors feed.
- Accounts: the relay presents the tunnel's own credential on the accounts back-call; the service admits a live tunnel credential as a household's server, voice only, scoped to its household (resolve 404s other households' people; guest answers the tunnel's household). No deployment secret on the voice side.
- Metrics: posted to the relay (`HttpMetricSink`), published with `MetricEvent.Origin` = the server's synthetic address. Heartbeats of two households' same service still share a roster row (4b).
