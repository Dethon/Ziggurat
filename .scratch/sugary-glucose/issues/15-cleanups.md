# 15: Loose ends found while building 01–13

**What to build:**
1. **Sugary turned off does nothing for that household** (decided 2026-11-18): the poller polls no link, the engine judges no watch, no prompt or alarm fires, for a household whose features turn Sugary off; the app is already refused. Sugary learns the features from the household's settings view (the way it already reads settings; a minute's keep, like the others). A household switching Sugary back on resumes at the next poll; watches' states are not advanced while off (a crossing while off does not fire later).
2. **One folder rule.** `SugaryPeopleFolders` duplicates `SugaryPath.FolderOf`'s rule; fold into one and drop the pinning test.
3. **One id across union members.** A create routed to one member must be refused when another member holds that id even if the caller cannot see it there (a non-grantee writing a home watch under an id a hidden glucose watch holds): the union asks the other members whether the entry exists at all, and a member that holds a hidden entry answers so without revealing it. Generic in `UnionFileSystem`; a member contract, not watch code.
4. **No household ids or LAN addresses in tracked tickets**: replace them in ticket 09's Comments with placeholders.

**Blocked by:** 13

Status: ready-for-agent

- [ ] Sugary off: no poll, no judgement, no fire; back on: resumes, nothing late.
- [ ] One folder implementation.
- [ ] A hidden id refuses a create routed to another member, with a message that reveals nothing about the hidden entry.
- [ ] Ticket 09 carries no real ids.
