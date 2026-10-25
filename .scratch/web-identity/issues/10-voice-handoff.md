# 10: Voice handoff

**What to build:** A login can start from nabu. On a voice turn, `web_login` also emits a fire into a new WebChat conversation for the person's WebChat name (from the identity map) whose prompt asks the agent to present the link; WebChat's existing end-of-stream Web Push pings the person. The tool's answer tells nabu the link is in WebChat. The login wake carries the voice reply target with the satellite's address, so the result is spoken on the satellite the request came from — as an announcement when no satellite session is live, never on every satellite. Spec: `../spec.md` ("Handoff", "Done and the wake").

**Blocked by:** 07

**Status:** done

- [x] A voice `web_login` emits one fire targeting a new WebChat conversation for the person, carrying the link (drained inbox test)
- [x] The tool answer on voice says the link is in WebChat; on text channels no WebChat fire is emitted
- [x] A person with no WebChat name in the map gets a refusal naming that
- [x] The wake's reply target keeps the satellite address; with no live session the existing announcement speaks it on that satellite only
