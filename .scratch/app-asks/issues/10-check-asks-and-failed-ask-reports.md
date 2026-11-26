# 10: Check asks and failed-ask reports

Spec: `.scratch/app-asks/spec.md` ("Checks"; "Runtime errors").

**What to build:** During `./check`, the page's asks are real turns that act for the author. They use
the check's own app session and call token, against the preview's manifest and instructions, and
count toward the cap. The check's summary lists failed asks. In a real browser, a failed ask is
reported to the app's runtime errors, reduced for a member.

**Blocked by:** 02, 07

**Status:** ready-for-agent

- [ ] A check's ask is admitted on the agent's own call token for the apps server, naming the author. Both headers together, the guest, or another server's token are refused (agent seam).
- [ ] A check's asks count toward the app's cap.
- [ ] The check summary lists failed asks (code and message) beside refused reads.
- [ ] The client library reports a failed ask to `/_errors`. A member's report is reduced to the ask's error code, and the author's keeps the message.
