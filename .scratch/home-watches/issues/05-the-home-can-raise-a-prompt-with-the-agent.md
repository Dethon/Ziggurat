# 05 — The home can raise a prompt with the agent

**What to build:** A POST from Home Assistant to a new endpoint on the Home Assistant server, guarded by the announce token, becomes a prompt in front of the agent named in the payload. The server becomes dual-role: its tool server stays as it is and it gains a channel server with the Broadcast policy and no outbound surface. The endpoint composes one notification through the shared fire planning: a conversation id from the watch id and the fire instant, sender `watch`, the creating agent, content whose first line states what fired (name, entity, from → to, when) followed by the rendered prompt, reply targets from `deliverTo`, `userId`, and a Watch origin. Delivered → 202. Bad or missing token → 401. Malformed payload → 400. No subscriber registered at all → 503 with a body saying no agent is connected, so the automation trace shows the loss. The agent registers the new channel beside scheduling; compose wires the announce token into the container; the bridge document becomes the "Home Assistant bridges" document with the second rest_command and the full-restart caveat.

**Blocked by:** 01 — Fire planning is shared.

**Status:** ready-for-agent

- [ ] Endpoint tests at the HTTP boundary: 202 with the composed notification drained from a real inbox (first line, agent id, reply targets, userId, origin, conversation id); 401; 400; 503 with no subscriber; delivery to a stale but registered subscriber.
- [ ] The server registers as a channel server with Broadcast and no outbound surface, and its channel-protocol tools are hidden from the model; the existing tool surface is unchanged.
- [ ] The agent's channel endpoints list the new channel; a fired prompt reaches the agent and its answer lands in a conversation titled after the watch (integration test over the MCP server fixture, as scheduling's).
- [ ] Compose passes the announce token to the container; no new `.env` placeholder.
- [ ] The bridges document describes both rest_commands and the local compose configuration and the integration seed carry the new one.
- [ ] Spec: `.scratch/home-watches/spec.md` § The callback, § Provisioning and rollout.
