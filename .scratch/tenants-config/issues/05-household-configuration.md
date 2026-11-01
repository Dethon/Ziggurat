# 05: Household configuration

**What to build:** The accounts service keeps each household's configuration as sections (settings, agents, keys, features, spaces), each a JSON document with a schema version migrated forward on read and save. Secrets are sealed under a per-household data key wrapped by the deployment master key, the one new `.env` secret. An internal API answers each caller its own view, taking the household from the verified call; a save publishes an invalidation over Redis. An operator command gets and sets a household's section from a file, sealing secrets on the way in. Spec: `../spec.md` ("Configuration in the accounts service").

**Blocked by:** 02

**Status:** done

- [x] A section saved under an old schema version is read migrated forward
- [x] A secret is readable only by the server its section belongs to, and never in the store in the clear
- [x] A caller cannot ask for another household's configuration than its verified call's
- [x] A save publishes an invalidation that drops a cached entry
- [x] Rotating the master key rewraps data keys and every secret still opens
- [x] The master key is a placeholder in `.env` wired to the accounts service alone (conformance)

Contracts: `.claude/rules/household-config.md`. Tests: `HouseholdConfigurationTests` (seam 1),
`HouseholdConfigurationClientTests` (the reader's cache against a real Redis), and the master key and
`AGENTKEYSURL` in `DeploymentConformanceTests`. The agent proves itself with a token of its own beside
`CALLERS__agent`, so the secret SexyTime's server shares opens no household's OpenRouter key.
