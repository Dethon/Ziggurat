# 05: Each service gets only its own secrets

**What to build:** No service receives the whole secrets file: each names the variables it reads, so the chat-client host and the dashboard no longer hold the OpenRouter key, the Home Assistant token or the logins key. The tracked secrets file stays placeholders, all empty, the announce token included. The OS overrides mount each project's own user secrets instead of the whole directory. Spec: `../spec.md` ("Per-service environment", "No working defaults").

**Blocked by:** 01

**Status:** done

- [x] The conformance check fails on any service taking the whole secrets file, and on the OpenRouter key, the Home Assistant token, the logins key or a Telegram token reaching a service that doesn't read it
- [x] Every placeholder is used by at least one service, and none has a value
- [x] Each override mounts only its project's user secrets, asserted by the check
- [ ] The stack comes up and the chat client answers a turn (manual check, not yet done: needs a compose up with real secrets; the WebChat E2E stack covers the per-service wiring it starts)
