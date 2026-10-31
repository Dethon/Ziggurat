# 03: Prompt sections as documents

**What to build:** Every prompt section becomes a text document with a small header carrying its purpose, priority band, token budget, audience and claims, and named placeholders for what the host fills in (date, name, description, language, time zone, satellites, formats, mounts, setup summaries). The manifest is read from the documents; the budget, staleness and snapshot tests run over them. No composed prompt changes. Spec: `../spec.md` ("Prompts, skills and templates as documents").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** done

- [x] Every section is a document and the manifest reads them
- [x] Every committed prompt snapshot is byte-identical
- [x] A document that drops a required placeholder fails validation
- [x] The budget and staleness tests run over the documents

Notes:
- The 21 sections live in `Domain/Prompts/Sections/<name>.md`, embedded into Domain; the format,
  the placeholder contract and the changed tier's reading are in `.claude/rules/prompts.md`
  ("A section is a document").
- No section carries a time zone today (only the scheduling skill does, which is 04's), so there is
  no `{{time_zone}}` placeholder yet; 06 adds the one it needs.
- The HA setup index is a file the mount builds, not a section, and stays code.
- The real mounts section (`filesystem_mounts`, built from a session's registry) is about 630
  tokens before its mount list, over its declared 600: the snapshots use a short sample in its
  place, so no test sees the real words' size. Unchanged here, since raising the budget would move
  every snapshot's header.
