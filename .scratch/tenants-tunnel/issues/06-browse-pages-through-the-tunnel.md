# 06: Browse pages through the tunnel

**What to build:** A person opening a login handoff link or the logins page gets them from the household's web search server at its site: the proxy routes `/browse/*` to the relay, which forwards the HTTP exchange and the live view's WebSocket down that household's web search tunnel, so the household's machine needs no reachable address. Spec: `../spec.md` ("The tunnel").

**Blocked by:** 03

**Status:** done

- [x] A handoff link, its claim and the live view's WebSocket work through the tunnel
- [x] The logins page shows and forgets remembered logins through the tunnel
- [x] The proxy routes `/browse/*` to the relay (conformance), and an offline web search answers a clear page

Which household a browse link is for: in this phase, the one household whose `websearch` tunnel is
open (`SoleHousehold`, `.claude/rules/tunnel.md`); none or several answer the offline page. The
deployment and smoke stack serve the offline page until ticket 11 runs web search tunnelled.
