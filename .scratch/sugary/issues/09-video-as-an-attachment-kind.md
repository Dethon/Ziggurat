# 09 — Video as an attachment kind

**What to build:** Ziggurat core. A third attachment kind, video, beside image and document. Attachment capability discovery maps the provider's `video` input modality to it. A clip's attachment reference can name still frames stored with it. At send time, for the model the turn resolved to, hydration sends the clip to a model that takes video, the stills and the message's photos to a model that takes only images, and refuses the turn for a model that takes neither. See the spec's "Video as an attachment kind".

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] Discovery marks a model as taking video when the provider lists `video`; the model capability tests cover it.
- [ ] A clip with stills hydrates as the clip on a video model and as the stills plus photos on an image-only model, never both.
- [ ] A patched turn is decided by the patched model, where the send already checks image capability a second time (ADR-0029).
- [ ] A model taking neither has the turn refused, as images are refused today.
- [ ] Wire tests beside the hydration-depth and page-image tests; no channel changes needed beyond carrying the new kind.
