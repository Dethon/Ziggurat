# 05: Smoke — a new household's vault, end to end

**What to build:** The smoke tier proves the feature on a running stack: a household onboarded through
the admin API mints a `vault` credential, a second kit vault (with a note of its own) is started on the
smoke stack with it, and the household's agent reads the note through its tunnel on its own key. The
smoke kit's vault is the home's, so this one is the new household's own. The manual test of 2026-11-02
stopped exactly here. Spec: `../spec.md` ("Testing Decisions", seam 3).

**Blocked by:** 02 (a household's agents reach the vault it set up), 04 (setting up takes effect on the next turn)

**Status:** done

- [x] `scripts/smoke-stack.sh up` (or the test itself) can start a vault for a second household with a credential minted at test time, and `down` removes it
- [x] The smoke test reads the new household's note through its agent, and the home's vault note is not what it reads
- [x] The stack stays local: nothing points at the deployment's host
