# 01: The admin area and its API

**What to build:** The tracer bullet. The accounts service gains an admin API under `/accounts/admin/*` that checks every request against the verified assertion (the owner role of the household it acts on, or the operator role) and records every change (who, what, when). The chat client gains an admin area offered only to owners, behind the same session. Its first page, the household's settings (name, time zone, language), works end to end over 4b's settings section. Spec: `../spec.md` ("Where it lives", "Features, spaces, settings").

**Blocked by:** None (can start immediately once 4b is merged)

**Status:** done

- [x] An owner reads and saves the household's settings from the admin area, and a turn then uses them
  (`HouseholdAdminE2ETests` saves and sees them; `AdminApiTests` proves the save is announced and the
  agents the household's next turn is built from carry the new zone and language)
- [x] A member gets no admin area and the API refuses them; an owner of another household is refused
  (no household route names a household: another household's owner reaches only their own)
- [x] Every change is recorded with who and when
- [x] The proxy routes `/accounts/admin/*` behind the session check and the internal API stays refused (conformance)

The API's conventions for 02–09 are `.claude/rules/admin.md`.
