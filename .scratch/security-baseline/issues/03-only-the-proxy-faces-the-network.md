# 03: Only the proxy faces the network

**What to build:** The base deployment publishes the proxy's 443, Redis on loopback, and the operator's LAN tools (qBittorrent's UI and peer port, filebrowser, Jackett, Home Assistant, Lemonade) on a LAN bind address the operator names, falling back to loopback. Everything else is reachable on the internal network only. An opt-in override publishes the debugging ports on loopback at today's numbers, and the editor debug tasks and stack-launch instructions use it. Spec: `../spec.md` ("Published ports").

**Blocked by:** 01, 02

**Status:** done

- [x] The conformance check fails while any base-file port other than 443 lacks a bind address, and passes after
- [x] Redis is published on loopback only
- [x] The LAN services bind to the named address, with a documented placeholder
- [x] The debug override publishes only on loopback, asserted by the check
- [ ] Running a project on the host against the stack with the override still works (manual check, not yet done: needs a compose up with real secrets)
