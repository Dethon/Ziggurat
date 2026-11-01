# 03: Keys, probes and onboarding

**What to build:** The admin area's keys page: OpenRouter, Idealista and Telegram bot tokens, each probed on save through a per-provider probe; a definite authentication failure refuses the save, an unreachable provider saves with a warning; a key's state and last check are shown, never the key (only its last characters); an authentication failure in use marks it rejected and re-probes. An owner whose household has no OpenRouter key lands on this page at sign-in. Spec: `../spec.md` ("Keys and probes", "Onboarding").

**Blocked by:** 01

**Status:** done

- [x] A rejected key is not saved and the owner is told why
- [x] A key saved while its provider is unreachable shows as unchecked with a warning
- [x] No API answer ever contains a saved key
- [x] A key failing in a turn shows as rejected
- [x] An owner with no OpenRouter key lands on the keys page

Notes: the state is a table beside the keys section (`HouseholdKeyChecks`), not fields in it, and the
turn's refusal reaches accounts on an internal route the agent proves as its own view; both reasons
are in `.claude/rules/admin.md` ("Keys and probes"). The E2E stack runs every person in the home
household and has no master key, so `HouseholdKeysE2ETests` submits only a rejected key and shows the
landing by answering the keys read as a keyless household's; saving, unchecked keys and the turn side
are seam 1 (`KeysAdminTests`, `MultiAgentFactoryHouseholdTests`). For 10: the smoke stack needs
`HOUSEHOLDCONFIG__MASTERKEY` set before a key can be saved, and `KEYPROBES__OPENROUTERURL` (with
`IDEALISTAURL`, `TELEGRAMURL`) pointed at its stub. Keys cannot be removed from the page (no route).
