# 09 — Video as an attachment kind

**What to build:** Ziggurat core. A third attachment kind, video, beside image and document. Attachment capability discovery maps the provider's `video` input modality to it. A clip's attachment reference can name still frames stored with it. At send time, for the model the turn resolved to, hydration sends the clip to a model that takes video, the stills and the message's photos to a model that takes only images, and refuses the turn for a model that takes neither. See the spec's "Video as an attachment kind".

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [x] Discovery marks a model as taking video when the provider lists `video`; the model capability tests cover it.
- [x] A clip with stills hydrates as the clip on a video model and as the stills plus photos on an image-only model, never both.
- [x] A patched turn is decided by the patched model, where the send already checks image capability a second time (ADR-0029).
- [x] A model taking neither has the turn refused, as images are refused today.
- [x] Wire tests beside the hydration-depth and page-image tests; no channel changes needed beyond carrying the new kind.

## Comments

**What was built.**
- `AttachmentKind.Video` is the kind for any `video/` media type, and `AttachmentKinds.All` includes
  it. `OpenRouterModelCapabilities` maps the provider's `video` modality to it. A silent catalogue
  stays permissive. Lemonade never lists it.
- `AttachmentReference.Stills` (`IReadOnlyList<string>?`) holds the ids of a clip's still frames. It
  is serialized only when set, so every other reference is byte-identical on the wire and in history.
- `ClipSelection` (`Domain/Agents/`), called from `AttachmentHydration` before any fetch, chooses for
  the model the send resolved to. `OpenRouterChatClient` builds `ModelInputs(model, images, video)`
  from `options.ModelId ?? model`, at the same spot the read-image capability is asked again.
  `MultiAgentFactory` answers video like images: the catalogue for a hosted model, the box's own
  list for a Lemonade model. `LemonadeChatClient` passes the answer through.
- On the wire a clip is `{"type":"input_video","video_url":"data:video/mp4;base64,…"}`, the
  Responses-API shape on OpenRouter's video page. The MEAI Responses adapter drops a video
  `DataContent` silently, so `VideoInputParts` turns each one into a raw `ResponseContentPart`, which
  the adapter passes through. This happens after truncation, on the copy that is sent.
- `MessageTruncator` counts a clip at a flat 6,000 tokens, not by its bytes.
- ADR `docs/adr/0045-a-clip-or-its-stills-is-chosen-at-the-send.md`. `.claude/rules/channels.md` got
  a clip paragraph, and its capability paragraph was amended.

**Decisions.**
- *Video model:* the clip, plus every attachment that is not one of its stills (photos, documents).
- *Image-only model:* the stills, plus every other picture, never the clip.
- *Refused* (`AttachmentRefusedException`, thrown before anything leaves, so no retry resends it):
  - a model that takes neither, as the spec says;
  - also an image-only model given a clip that names **no stills present on the message**, since
    dropping the clip silently would be the answer story 42 forbids.

  The exception's message names the model and the file. The channel receives it as the turn's error
  (`ErrorContent` → a `send_reply` of type `Error`, text `An error occurred: <message>`).
- *Past the hydration distance,* a single placeholder names the clip on every model, with none per
  frame, and nothing is refused.
- *Shared rule:* `AttachmentCapability.Refusal` refuses a `video/` type only for a model that takes
  neither video nor images. Stills stand in elsewhere, and the send refuses a clip that came without
  them.
- *Telegram* still refuses a video ("I can only read images and PDFs"): it neither normalises clips
  nor takes stills. WebChat's upload store does not allow `video/` types, so it is unchanged.
- *Not verified live:* OpenRouter documents `input_video` on the Responses wire only with a YouTube
  URL. A data URL is documented for Chat Completions' `video_url`, and the standalone eval used it
  against Gemini. If the Responses wire refuses a data URL, only `VideoInputParts` changes. Ticket
  08's eval or ticket 12's E2E is where this first gets checked against a real model.

**Tests.**
- `OpenRouterChatClientVideoTests`: a real `McpAgent` over a real `OpenRouterChatClient`, with bodies
  read off the HTTP handler. Cases covered:
  - the clip and not its stills on a video model, and stills plus photos and no clip on an
    image-only one;
  - a patched turn decided by the patched model, both ways round;
  - refusals for a neither-model, for a patch onto one, and for a stills-less clip on an image-only
    model, each with zero requests sent;
  - the clip resent on a follow-up turn through a history that round-trips as JSON;
  - one placeholder past the distance on every model.
- `MultiAgentFactoryClipTests`: the production wiring, for a hosted model and a Lemonade one.
- Also: the capability mapping in `OpenRouterModelCapabilitiesTests`, the shared refusal in
  `CapabilityRefusalTests`, the channel-wire round trip in `ChannelProtocolTests`, and the estimate
  in `MessageTruncatorTests`.

**How a channel sends a clip with stills (for ticket 10).** Store the normalised clip and each still
as its own attachment in the channel's upload store, answered by `fetch_attachment` like any other.
Put all of them in **the same** `ChannelMessageNotification.Attachments` list, alongside the
overhead photo(s). On the clip's reference, set `Stills` to the stills' ids:

```csharp
var stills = frames.Select((f, i) => new AttachmentReference
{
    Id = f.Id, FileName = $"still-{i + 1}.jpg", MediaType = "image/jpeg", SizeBytes = f.Length
}).ToList();
var clip = new AttachmentReference
{
    Id = clipId, FileName = "meal.mp4", MediaType = "video/mp4", SizeBytes = clipLength,
    Stills = [.. stills.Select(s => s.Id)]
};
Attachments = [clip, .. stills, .. photos]   // order is free; photos are ordinary image references
```

On the wire (camelCase) the clip reads `{"id":…,"fileName":"meal.mp4","mediaType":"video/mp4",
"sizeBytes":…,"stills":["…","…"]}`, and stills and photos carry no `stills` key. Do **not** list the
stills only inside the clip: a still must also be an attachment of the message, because hydration
fetches it as one. The channel needs no model knowledge of its own. To refuse early, it can ask
`AttachmentCapability.Refusal` with the media types, which refuses a clip only for a model that
takes neither. Everything else is decided per turn at the send, including follow-up turns, which
resend the clip (or its stills) within the 20-message distance.
