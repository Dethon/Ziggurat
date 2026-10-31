# 07: Each household pays for its own model calls

**What to build:** Every model call made for a household uses its OpenRouter key: turns, workers, memory extraction (the request names the household), dreaming and its judge (walking households and their people), skill preloading and the exec screen. The home household uses the deployment key; a household with no key refuses its turns with a message the person sees; housekeeping calls (keep-alives, model capabilities) stay on the deployment key. Spec: `../spec.md` ("Agents per household").

**Blocked by:** 06

**Status:** done

- [x] A household's turn, its worker and its memory extraction and dreaming are sent with that household's key
- [x] A household with no key gets a clear refusal and no model call is made
- [x] The home household's calls use the deployment key
- [x] No background work reads its household from ambient state

Notes: the contracts are `.claude/rules/household-config.md`, "Who pays for a household's model calls".
Dreaming walks a new agent-only `people` view of the accounts service. A shared server's own Jev use
(voice's approval reader, the web search modal judge) is counted under the call's household but asked
on the server's own key, since no shared server is given a household's key.
