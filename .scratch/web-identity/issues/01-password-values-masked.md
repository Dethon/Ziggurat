# 01: Password values masked in the accessibility snapshot

**What to build:** No browse tool ever returns what was typed into a password field. Today the accessibility snapshot maps a password input to a textbox and prints its live value, so `web_snapshot`, the snapshot composed with `web_browse` and — most often — `web_action`'s automatic diff after a fill hand the typed secret straight back into the history. A password field's value is reported as masked in every context, whether or not the page is browsed as a person. Spec: `../spec.md` ("Password values").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] A password field filled through `web_action` shows a masked value in the action's diff, never the typed text
- [ ] `web_snapshot` and the snapshot composed with `web_browse` mask a password field's value, including one filled by the page's own script
- [ ] An empty password field still reads as empty, so the model can tell filled from unfilled
- [ ] Ordinary text fields keep reporting their value as today
- [ ] Tested against real Camoufox on a hermetic page (prior art: the browser integration tests)
