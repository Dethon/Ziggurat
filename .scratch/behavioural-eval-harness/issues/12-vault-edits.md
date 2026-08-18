# 12 — Vault edits preserve Obsidian syntax

**What to build:** the agent edits the user's notes without quietly breaking them.

Frontmatter survives with only the keys the user mentioned changed. Wikilinks, embeds, block ids,
callouts and template placeholders survive untouched — a wikilink is never "fixed" into Markdown
link syntax. A new note lands in the deepest existing folder whose topic matches, never at the
root by default and never in a newly invented top-level folder.

**Blocked by:** 04, 05.

**Status:** ready-for-agent

- [x] A temp directory seeded as a vault carries frontmatter, wikilinks, embeds, block ids and a
      callout, plus a folder tree with an obvious home for the note under test.
- [x] An edit to a note leaves every one of those intact; the assertion reads the file's content
      after the turn.
- [x] A rename updates incoming wikilinks in the same turn.
- [x] A new note lands in the matching existing folder, not the root and not a new folder.
- [x] The configuration directory is never written: its files are declared unchanged in the edit
      scenario. Whether it was *listed* is not the agent's to control — the mount returns what it
      returns for a glob of the vault root — so what is asserted is that nothing wrote to it.
- [x] Every scenario was demonstrated red once. Only the rename earned its citation: with the
      filenames-are-identifiers rule deleted the model renamed the file and left every incoming
      link pointing at the old name. The syntax-preservation and note-placement demonstrations
      stayed green, and `ClaimExemptions` says so.
