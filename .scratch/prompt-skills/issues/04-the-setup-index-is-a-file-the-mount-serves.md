# 04 — The setup index is a file the mount serves

**What to build:** The Home Assistant mount serves the setup index as a markdown file at its root, built on every read from the same catalog, watches and satellite sources the served prompt uses today, with the same rooms, entities, action table by class, watches line and satellite line. The served prompt no longer appends it. The home prompt's stub says to read the file before a home task and the guide says to re-read it when a name does not resolve. Home Assistant scenarios permit or require the read. Behaviour is otherwise unchanged; this is a move, not a skill.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] With the fake client, listing the mount root shows the file; reading it yields the text the summary builder yields for the same catalog; adding an entity to the fake and reading again shows it.
- [ ] The served home prompt is the static guide alone, and the prompt fixture's served text equals it.
- [ ] Home Assistant, watch and music scenarios permit the read; the ones that need names to resolve require it; the full family passes at its previous rate with the index out of the prompt.
- [ ] Spec: `.scratch/prompt-skills/spec.md` § The setup index.
