# 11: Operator turns the apps feature on

Spec: `.scratch/app-spaces/spec.md` ("The apps server").

**What to build:** The operator turns the `apps` feature on or off for a household from the dashboard's household page. The owners' features page shows it but cannot change it. With the feature on, the household's own agents are offered `/apps` from their next turn.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] The operator's household page sets and clears `apps`.
- [ ] An owner's save of the features section never changes `apps`, even when it sends a value.
- [ ] A household without the setting reads it as off.
- [ ] Covered by the operator and owner admin integration tests.
