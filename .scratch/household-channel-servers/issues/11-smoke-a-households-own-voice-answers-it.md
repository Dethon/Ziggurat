# 11: Smoke: a household's own voice answers it

**What to build:** The smoke voice check gains a household case beside the home's:
- A non-home household is onboarded and adds the voice template's agent through the admin API.
- Its voice credential is minted, and a second kit voice server and test satellite start with it.
- The household's satellite hears its own agent answer a question.
- A timer set by voice rings on that household's satellite.
- A household with voice and no voice agent hears the refusal instead of two minutes of silence.

The home's voice check runs beside it, unchanged. The CLAUDE.md smoke-tier description mentions the
household case. Spec: `../spec.md`.

**Blocked by:** 02, 03, 06, 07

**Status:** done

- [x] The household's satellite hears its own voice agent's answer
- [x] A timer set by voice in that household rings on its satellite
- [x] A household with voice and no voice agent hears the refusal
- [x] The home's voice check still passes beside it
