# 09: Sharing approves asks

Spec: `.scratch/app-asks/spec.md` ("The approval covers asks").

**What to build:** An app's reach gains `asks`. When an owner shares an app as an app space, they see
and approve the servers its asks may use. A shared app's turns are offered only the approved servers.
A publish that asks beyond the approval says what waits on an owner, as reads do.

**Blocked by:** 05

**Status:** ready-for-agent

- [ ] `AppReach` carries `asks`; `Within` and `Beyond` cover it.
- [ ] The section's approved reach saves and reads it back, with shape checks.
- [ ] The published-apps listing and the owner's sharing page show the app's asks, and the approval saves them (owner admin seam).
- [ ] On a shared app the message carries only the approved servers, for every viewer, the author too (origin seam).
- [ ] An unshared app's author gets the whole manifest.
- [ ] `./publish` of a shared app asking new servers says they wait on an owner, and the publish stands.
