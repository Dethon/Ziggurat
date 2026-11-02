# 04: Watches, downloads, logins and browse pages for every household

**What to build:** With 01 and 02 in place, the other channel servers work for any household. This
ticket closes the gaps that are not about following. A browse link names its household, and the
forwarder opens that household's web search tunnel (`TunnelRegistry.Find`) instead of the home's
(`FindHome`). `redelegate` and `retire-signing-keys` ask every household's open channel servers that
hold delegations, list each by household and name, and report one that is not connected as "could not
be asked". The Jonas template's header comment and its "web_login does not reach one other than the
home" note are rewritten. Spec: `../spec.md`.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] A non-home household's Home Assistant watch fire starts its turn (tunnelled round trip with the real Home Assistant server, as `TunnelledChannelTests` does for the home)
- [ ] A non-home household's library download notification starts its turn
- [ ] A non-home household's finished web login wakes its conversation
- [ ] A browse link from a non-home household's agent opens that household's web search (`BrowsePagesTests`), and one for an offline household server shows the offline page
- [ ] `redelegate` reaches a non-home household's Home Assistant server and reports an offline one as not asked (`RedelegationOverTunnelTests`)
- [ ] `jonas.md`'s header and `.claude/rules/tunnel.md` no longer say channels are the home's alone
