# 01: A deployment conformance check

**What to build:** A test-side reader for the deployment's compose files and proxy configuration, so the later tickets each add the assertions for what they close. The existing check that the proxy never routes the exec bridge moves onto it unchanged. Prefactor for this spec's new seam. Spec: `../spec.md` (Testing Decisions, seam 1).

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] The bridge assertion runs on the shared reader and still passes
- [ ] The reader exposes, per service, its published ports, its environment, its volumes and whether it takes the whole secrets file, and, per proxy host, its routes
- [ ] The reader is exercised by a test against the real deployment files, not a copy
