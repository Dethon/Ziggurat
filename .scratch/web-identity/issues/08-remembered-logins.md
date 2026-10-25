# 08: Remembered logins

**What to build:** A login survives restarts and later conversations unless the person says otherwise. The live view gains a Remember box, ticked by default. A person's state — Playwright storage state (cookies, localStorage, IndexedDB) — is stored in Redis encrypted with a key from secrets (placeholder in the compose env file), with the person's sites: remembered or live-only, handed off when, last used when. Unticked marks every registrable domain the handoff tab visited as live-only, and every save leaves those out. The state is re-saved, debounced, after `asMe` browses and actions so rotated cookies persist; no expiry of our own. A person context is restored from the store on first use after a restart. No model tool reads or writes the store. Spec: `../spec.md` ("Remembered state").

**Blocked by:** 06

**Status:** done

- [x] Remembered: after the browser reconnects, an `asMe` browse of the site is still logged in (real Camoufox)
- [x] Unticked: the site is logged in until the browser restarts, never written to the store, and its cookies are absent from saves made afterwards for other sites
- [x] The stored record is unreadable without the key; a wrong or missing key refuses to restore and says so rather than starting silently logged out
- [x] A cookie the site rotates during `asMe` use is in the next restore
- [x] No MCP tool exposes the store
