# 07: Each household pays for its own model calls

**What to build:** Every model call made for a household uses its OpenRouter key: turns, workers, memory extraction (the request names the household), dreaming and its judge (walking households and their people), skill preloading and the exec screen. The home household uses the deployment key; a household with no key refuses its turns with a message the person sees; housekeeping calls (keep-alives, model capabilities) stay on the deployment key. Spec: `../spec.md` ("Agents per household").

**Blocked by:** 06

**Status:** ready-for-agent

- [ ] A household's turn, its worker and its memory extraction and dreaming are sent with that household's key
- [ ] A household with no key gets a clear refusal and no model call is made
- [ ] The home household's calls use the deployment key
- [ ] No background work reads its household from ambient state
