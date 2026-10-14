# 05 — Photos reach the estimate

**What to build:** She photographs a meal (one or several photos of the same plate, with or without words) and the `sugary` agent's model sees the photos. The Sugary channel keeps the bytes in an upload store of its own, sends attachment references on the turn, and answers the agent's attachment fetch, as the SignalR channel does. See the spec's "The channel role".

**Blocked by:** 04

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] One photo and several photos of the same meal both reach the model, hydrated on the way out like any attachment.
- [ ] The history keeps references, never bytes.
- [ ] A photo too large for the model is refused before it is sent, with a reason the app shows.
- [ ] A model without image input has the turn refused, never answered as if no photo was sent.
- [ ] The upload store's retention outlasts the hydration distance, as ADR-0020 requires.
- [ ] Tests at the server's boundary fetch the attachments the way the agent does.
