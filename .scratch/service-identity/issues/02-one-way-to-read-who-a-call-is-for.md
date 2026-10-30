# 02: One way for a server to read who a call acts for

**What to build:** Every server learns who a call acts for in one place. Today the browse, library and approval tools and the filesystem caller each parse the turn out of the call's raw `_meta`; they all go through one accessor over the call itself instead, with no behaviour change, so a later step can make that accessor answer the verified token and no tool can read around it. Prefactor. Spec: `../spec.md` ("Servers").

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] Every server-side read of the turn goes through the accessor, and a check fails if a tool parses the raw `_meta` itself
- [ ] The servers' existing tests pass unchanged
- [ ] The accessor answers nothing for a call with no turn, as the parse did
