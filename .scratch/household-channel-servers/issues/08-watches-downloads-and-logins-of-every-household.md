# 08: Watches, downloads and logins of every household start their turns

**What to build:** With a household's dual-role servers followed and its deliveries kept in the
household, each of these starts a turn for its own household:
- a non-home household's Home Assistant watch firing;
- its library saying a download finished;
- its web search saying a handed-off login is done.

A watch's effects are delivered where the watch says. The Jonas template's header comment stops
saying channels are the home's alone. Spec: `../spec.md`.

**Blocked by:** 03

**Status:** ready-for-agent

- [ ] A non-home household's Home Assistant watch fires a turn for that household, through the real Home Assistant server over its tunnel (round trip), with its delegation checked
- [ ] Its library's download notice starts that household's turn
- [ ] Its web search's finished login wakes that household's conversation
- [ ] A watch's delivery lands on the household's own channel
