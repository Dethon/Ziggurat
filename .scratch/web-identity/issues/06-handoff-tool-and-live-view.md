# 06: Handoff tool and live view

**What to build:** When a turn needs a login, the model calls `web_login(url)`. The browse server opens that url in a fresh handoff tab in the person's context — outside the browse session's tab pool, so no ref, browse or action reaches it — and mints a handoff ticket (single use, ten minutes, bound to the person, the conversation context and the tab, kept in Redis). It answers with an absolute link under the assistants host's `/browse` path, built from a per-deployment public base address. Opening the link spends the ticket and binds the page to that one handoff; the page shows the tab live (Playwright page screencast over a WebSocket) and forwards the person's mouse, wheel and keyboard; popups join the view. Done closes the handoff tab; the login then lives in the person context in memory. An expired or abandoned ticket drops the tab and does nothing else. The tool refuses on a fire's turn. Spec: `../spec.md` ("Handoff", "Live view").

**Blocked by:** 03, 05

**Status:** done

- [x] Demo: hand off a hermetic login page through the link, press Done, and the next `asMe` browse of that site is logged in
- [x] A second open of a link, or an open after ten minutes, is refused; the handoff tab is gone after expiry
- [x] The handoff tab is unreachable from any ref, browse or action of the browse session
- [x] Forwarded typing lands in a field and frames arrive (real Camoufox)
- [x] `web_login` refuses on a schedule or watch fire's turn and for an unmapped user
- [x] Caddy routes `/browse` on the assistants host to the browse server; the public base address is a compose environment entry with a placeholder
- [x] Ticket rules and refusals tested on the server surface with a fake browser (prior art: the websearch session scope tests, the watch-fired endpoint tests)
