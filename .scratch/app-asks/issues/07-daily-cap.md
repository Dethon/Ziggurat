# 07: The daily cap

Spec: `.scratch/app-asks/spec.md` ("The cap").

**What to build:** Asks are counted per household and app over a rolling 24 hours, when the turn is
sent. Over the limit, an ask is refused with the cap code, which names the limit. Counts survive an
apps-server restart. The limit for now is the deployment's default, an apps-server setting.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] The Nth ask within 24 hours succeeds and the next is refused with the cap code naming the limit (origin seam, fake clock).
- [ ] An ask refused before sending counts nothing.
- [ ] Every viewer of a shared app shares one count. Another household's copy of the app, or another app, has its own.
- [ ] The window rolls: asks older than 24 hours stop counting.
- [ ] Counts are durable on the apps server's volume and survive a restart.
