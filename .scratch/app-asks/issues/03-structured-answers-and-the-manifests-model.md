# 03: Structured answers and the manifest's model

Spec: `.scratch/app-asks/spec.md` ("The origin's ask route"; "The app manifest gains three fields").

**What to build:** An app passes a strict JSON schema on an ask and gets parsed JSON back, as
Sugary's estimate does (ADR 0044). An app whose manifest names `model` asks on that model of the
building agent's patchable models.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] `apps.ask(text, { schema: { name, schema } })` puts the schema on the message as the response format. The agent sends it strict and as written (origin and agent seams).
- [ ] A reply that parses resolves as the parsed value. One that does not is the `invalid_reply` code; its raw text reaches the author's preview only, never a member.
- [ ] A schema that is not an object, or is over the size bound, is refused before any turn.
- [ ] `AppManifest` takes `model`. It travels as the config patch, and a model the agent cannot patch to answers on the agent's own model, as any patch does (agent seam).
