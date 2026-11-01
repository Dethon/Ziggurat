# 08: Usage

**What to build:** A usage view per household and month in the admin area: tokens and cost per agent and per person, and Lemonade's speech seconds, characters spoken and embeddings, read from 4b's per-household metrics; the dashboard's existing pages gain a household filter for the operator. Spec: `../spec.md` ("Usage").

**Blocked by:** 01

**Status:** done

- [x] An owner sees their household's tokens and cost per agent and person for a month
- [x] Lemonade use appears for the household
- [x] The operator filters every dashboard page by household
- [x] An owner never sees another household's usage

Notes: the admin API reads the figures from the Observability host's usage route (`/api/usage`) with an
assertion the accounts service signs for the owner on the `usage` channel, so no secret and no proxy route;
the route answers the assertion's household alone (`HouseholdUsageAdminTests`). A month is UTC days, and
daily roll-ups expire after thirty days, so history is about a month deep. The dashboard filter lasts the
tab's life and is not saved.
