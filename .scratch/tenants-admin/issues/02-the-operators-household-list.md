# 02: The operator's household list

**What to build:** The dashboard gains the operator's household list: create a household (name, owner's display name) and get the owner's passkey link; see each household's owners, members, connected servers and last activity; mint a recovery link for any household's owner. A new household starts with 4b's defaults. Spec: `../spec.md` ("Onboarding").

**Blocked by:** 01

**Status:** done

- [x] The operator creates a household and gets an owner link that enrolls a passkey into it
- [x] The list shows owners, members, connected servers and last activity
- [x] A recovery link for an owner works and is recorded
- [x] Nobody but the operator can reach the list

Done: `HouseholdListAdmin` (`GET`/`POST /accounts/admin/operator/households`,
`POST /accounts/admin/operator/households/{householdId}/owners/{userId}/recovery`), the dashboard's
`/households` page, `HouseholdListAdminTests`, a ceremony in `PasskeyCeremoniesTests`, and
`HouseholdListE2ETests` on `DashboardAdminE2EFixture`. Connected servers: the accounts service asks the
agent's listing with `CALLERS__agent` (no new secret). Last activity: the latest session seen by the
session check (Telegram and voice use does not move it). See `.claude/rules/admin.md`, "The household list".
