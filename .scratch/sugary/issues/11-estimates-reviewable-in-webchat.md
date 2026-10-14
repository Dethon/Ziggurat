# 11 — Estimates reviewable in WebChat

**What to build:** Each conversation the Sugary channel opens also appears as a WebChat topic under the `sugary` agent, showing her message with its photos (and clip, once filming exists) and the agent's reply as sent; a structured reply shows as its JSON text. This extends the fan-out that today serves only agent-initiated messages to messages a channel sends on a person's behalf when the channel asks for a review copy, with the Sugary channel kept as the primary target. See the spec's "WebChat review".

**Blocked by:** 05

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] The topic is minted on the conversation's first turn and later turns attach to it; the Sugary channel stays first, so the app never waits on WebChat.
- [ ] Decide and record how WebChat shows attachments whose bytes rest in the Sugary channel's store (WebChat shows only its own uploads today); the photos are visible in the topic.
- [ ] The topics follow the normal topic retention.
- [ ] Tests at the chat monitor's delivery resolution, beside the existing conversation-context tests.
- [ ] If the widened fan-out warrants it, an ADR records it.
