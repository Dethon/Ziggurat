# 10: The smoke tier onboards a household

**What to build:** A fresh household is onboarded end to end on a real stack: created from the dashboard, its owner signed in by the link, a key set against a stub provider probe, its agent named, a reply received, then suspended and refused. Spec: `../spec.md` (Testing Decisions, seam 5).

**Blocked by:** 02, 03, 05, 09

**Status:** done

- [x] A household created on a fresh smoke stack gets a reply from its named agent
- [x] Suspending it refuses its next turn
- [x] The smoke suite passes on a fresh `scripts/smoke-stack.sh up`

Done: `StackSmokeTests.AHouseholdTheOperatorCreates_IsAnsweredByTheAgentItsOwnerNamed_UntilItIsSuspended`. The owner is
signed in by `mint-session --user` (a passkey ceremony needs an authenticator; the link's enrolment is
`PasskeyCeremoniesTests`'). The household's OpenRouter key is the deployment's own, read from the agent's user
secrets on this machine, so the probe is the stub's (`smoke-keys`) and the one turn is real. Smoke 23/23 on a
fresh `up`.
