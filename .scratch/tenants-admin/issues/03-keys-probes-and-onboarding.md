# 03: Keys, probes and onboarding

**What to build:** The admin area's keys page: OpenRouter, Idealista and Telegram bot tokens, each probed on save through a per-provider probe; a definite authentication failure refuses the save, an unreachable provider saves with a warning; a key's state and last check are shown, never the key (only its last characters); an authentication failure in use marks it rejected and re-probes. An owner whose household has no OpenRouter key lands on this page at sign-in. Spec: `../spec.md` ("Keys and probes", "Onboarding").

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] A rejected key is not saved and the owner is told why
- [ ] A key saved while its provider is unreachable shows as unchecked with a warning
- [ ] No API answer ever contains a saved key
- [ ] A key failing in a turn shows as rejected
- [ ] An owner with no OpenRouter key lands on the keys page
