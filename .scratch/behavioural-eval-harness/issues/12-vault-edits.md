# 12 — Vault edits preserve Obsidian syntax

**What to build:** the agent edits the user's notes without quietly breaking them.

Frontmatter survives with only the keys the user mentioned changed. Wikilinks, embeds, block ids,
callouts and template placeholders survive untouched — a wikilink is never "fixed" into Markdown
link syntax. A new note lands in the deepest existing folder whose topic matches, never at the
root by default and never in a newly invented top-level folder.

**Blocked by:** 04, 05.

**Status:** ready-for-agent

- [ ] A temp directory seeded as a vault carries frontmatter, wikilinks, embeds, block ids and a
      callout, plus a folder tree with an obvious home for the note under test.
- [ ] An edit to a note leaves every one of those intact; the assertion reads the file's content
      after the turn.
- [ ] A rename updates incoming wikilinks in the same turn.
- [ ] A new note lands in the matching existing folder, not the root and not a new folder.
- [ ] The configuration directory is never listed or written.
- [ ] Every scenario cites its claims and was demonstrated red once.
