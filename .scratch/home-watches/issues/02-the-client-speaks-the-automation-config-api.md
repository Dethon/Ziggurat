# 02 — The client speaks the automation config API

**What to build:** The Home Assistant client can list the home's automations (with each one's entity state, so on/off and last-triggered come along), read one automation's config by id, write one by id (create or replace, which reloads it), and delete one by id. A config Home Assistant rejects surfaces as a typed error carrying Home Assistant's own message, never a bare status code. The unit fake client and the eval's fake home both gain an automation store answering the same operations, and the fake home counts automations carrying the assistant prefix in its snapshot so a later scenario can declare "one more watch" as its change.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] Against the seeded container: write an automation, read it back, see it listed with its entity state, delete it, see it gone.
- [x] Against the seeded container: writing an automation with an invalid trigger fails with an error whose message is Home Assistant's validation text.
- [x] Unit tests pin the HTTP shape of each operation the way the calendar and history client tests do.
- [x] The unit fake client stores automations and records writes and deletes; the eval fake home serves the same operations from a store and exposes a watch-count snapshot key.
- [x] Spec: `.scratch/home-watches/spec.md` § Home Assistant client.
