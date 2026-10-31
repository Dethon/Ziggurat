# 03: Prompt sections as documents

**What to build:** Every prompt section becomes a text document with a small header carrying its purpose, priority band, token budget, audience and claims, and named placeholders for what the host fills in (date, name, description, language, time zone, satellites, formats, mounts, setup summaries). The manifest is read from the documents; the budget, staleness and snapshot tests run over them. No composed prompt changes. Spec: `../spec.md` ("Prompts, skills and templates as documents").

**Blocked by:** None (can start immediately once 4a is merged)

**Status:** ready-for-agent

- [ ] Every section is a document and the manifest reads them
- [ ] Every committed prompt snapshot is byte-identical
- [ ] A document that drops a required placeholder fails validation
- [ ] The budget and staleness tests run over the documents
