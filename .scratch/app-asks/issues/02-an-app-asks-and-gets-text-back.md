# 02: An app asks and gets text back

Spec: `.scratch/app-asks/spec.md` ("Solution"; "The apps server becomes a channel"; "The origin's ask route"; "What the message carries"; "What the agent does with an app turn").

**What to build:** The tracer bullet. An app whose manifest names an `instructions` file calls
`await apps.ask("…")` and gets the agent's text back.
- **The apps server.** It is dialled as the `apps` channel endpoint. `POST /_ask` on the origin is
  settled as a read is, then sent as a channel message. The message names the building agent, carries
  the viewer's `apps` assertion and the app turn (the app id, the instructions text, and no servers
  yet). The apps server then waits on the reply.
- **The agent.**
  - It honours the app turn only from the `apps` endpoint and admits the turn on the `apps`
    assertion.
  - The system prompt is the app's instructions plus the sections every agent carries, not the
    persona. The turn has no tools.
  - It neither recalls nor extracts memory, makes no WebChat topic, and counts usage on channel
    `apps` with the app named.

**Blocked by:** 01

**Status:** ready-for-agent

- [ ] `AppManifest` takes `instructions`. Its shape is judged on every way in, and `./publish` refuses a path that is no file of the commit, or one over the size bound.
- [ ] The published origin asks with the served commit's instructions and the preview with the working tree's (origin seam).
- [ ] An app without instructions is refused `permission_denied`; anyone who may not open the app gets the one 404 (origin seam).
- [ ] The message names the building agent, carries the viewer's assertion and the app turn, and is sent under its own turn key (origin seam).
- [ ] The page giving up cancels the turn. The timeout cancels it and answers the timeout code. The agent unreachable answers unavailable (origin seam).
- [ ] The model is sent the app's instructions with no persona, custom instructions, subagents, channel, voice or memory sections (agent seam, FakeOpenRouter).
- [ ] Neither recall nor an extraction enqueue happens for an app turn (agent seam).
- [ ] An app turn on any channel but `apps` is ignored and logged (agent seam).
- [ ] Admission refuses: no assertion, a non-`apps` assertion, the guest, a suspended household, a disabled member, the `apps` feature off, and a building agent the household no longer has (named). Each comes back to the app as `AskError` (agent seam).
- [ ] No topic is minted, and usage is counted on channel `apps` under the viewer with the app id (agent seam).
- [ ] The client library exposes `apps.ask` and `apps.AskError`, from the classic script and the module.
- [ ] Wiring: the `apps` channel endpoint in the agent's settings; conformance tests updated.
