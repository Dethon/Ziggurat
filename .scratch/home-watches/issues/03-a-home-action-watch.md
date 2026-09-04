# 03 — A home-action watch

**What to build:** Asked "close the blinds when the living-room temperature goes above 27", the agent writes `/ha/watches/<id>/watch.json` with a name, Home Assistant triggers, optional conditions and one `actions` effect, and the home gains a real automation with the assistant prefix and the watch's metadata in its description. The agent can list watches, read one back (the file round-trips from the automation), edit it in place under the same id, delete it, and read a `status.json` with created-at, last-triggered, the automation entity and spent. Malformed files, empty triggers or effects, an unknown effect kind and anything Home Assistant rejects are write errors that name the problem. Hand-made automations never appear under the subtree and cannot be touched through it; the rest of the mount stays read-and-exec. The setup index lists watches and their count; the guide's new section teaches the file shape, the common trigger shapes, and the boundary with alarms, timers and schedules, with claims declared beside the prose.

**Blocked by:** 02 — The client speaks the automation config API.

**Status:** ready-for-agent

- [ ] Over the fake client: create, glob, read back, edit in place (same automation id, no second automation), delete; `status.json` fields; the rendered automation carries the prefix, the alias, `mode: single`, triggers and conditions verbatim, and the actions verbatim.
- [ ] Every write error in the spec is a test, including Home Assistant's own message on a rejected trigger.
- [ ] A hand-made automation in the fake store is absent from `/ha/watches` and still present under the entities tree.
- [ ] Writes anywhere else in the mount are still refused.
- [ ] The setup index has a `watches:` line; the setup summary test pins it.
- [ ] The guide teaches watches; the prompt snapshot pins the section; each rule a scenario checks is a declared claim and the claim-coverage test passes.
- [ ] Eval scenario: the home-action watch above, declaring the watch count as its change, with a claim on read-back.
- [ ] Spec: `.scratch/home-watches/spec.md` § The watch record, § The mount, § Rendering the automation, § Prompts and discovery.
