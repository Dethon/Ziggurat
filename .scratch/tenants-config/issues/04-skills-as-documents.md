# 04: Skills as documents

**What to build:** Every skill becomes a document in the same form, served by its server as today; the skill index and the skill snapshots are unchanged. Spec: `../spec.md` ("Prompts, skills and templates as documents").

**Blocked by:** 03

**Status:** done

- [x] Every skill is a document served by its server
- [x] Every committed skill snapshot and the skill index are unchanged

Notes: the documents are `Domain/Prompts/Skills/<name>.md`, read by `PromptDocument.AsSkill`, in
Domain rather than beside their servers — the server still ships each skill (ADR 0039), but the
agent, the eval, Jev's preload and the budget tests read the declaration from the manifest, and no
server is referenced by them. The skill classes are handles (`Text`/`For(...)` unchanged, so no
server's `ConfigModule` changed). The manifest lists skills by name; the prompt fixture takes each
server's registration order, which is what the agent snapshots always showed.
