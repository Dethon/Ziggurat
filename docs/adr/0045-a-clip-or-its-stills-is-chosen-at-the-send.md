# 0045 — A clip or its stills is chosen at the send

Status: accepted
Date: 2026-10-14

## Context

Sugary's best measured input is a short clip around the plate: on 14 weighed dishes it cut Gemini's
mean error from about 11 g to about 8 g (`.scratch/sugary/spec.md`). Ziggurat had two attachment
kinds, image and document, and a clip was neither. The frames from the same clips helped Gemini a
little and other models not at all, but they are what a model that takes only pictures can be
given, and the spec does not want a future model ruled out by its input types.

So a clip arrives with still frames the channel took from it, and which of the two the model gets
depends on the model. That model is not settled when the channel emits: a config patch moves one
turn onto another model, and a follow-up turn resends the clip to whatever model it runs on. The one
place that knows is the send, which already asks image capability a second time for read images
(ADR-0029) because the wire rejects a whole request carrying a part the model cannot take.

The Responses adapter (`Microsoft.Extensions.AI.OpenAI` 10.10.1) has no video part: a video
`DataContent` is dropped from the request without a word (probed 2026-10-14). OpenRouter documents a
video part on both wires — `video_url` on Chat Completions, `input_video` with a string `video_url`
on Responses — and a model takes it when its `input_modalities` lists `video`.

## Decision

**Video is a third attachment kind, a clip's reference names its stills, and the send picks the clip
or the stills for the model the turn resolved to.**

- `AttachmentKind.Video`, for any `video/` media type. `OpenRouterModelCapabilities` maps the
  provider's `video` modality to it, beside `image` and `file`; a silent catalogue stays permissive
  for it too. A Lemonade model never lists it.
- `AttachmentReference.Stills` holds the ids of the still frames taken from a clip. The stills are
  ordinary image references in the same message's attachment list. The property is left off the
  wire and out of the history when null.
- **The choice is made at the send.** `AttachmentHydration` asks `ClipSelection` before it fetches
  a byte, given `ModelInputs` (the model, whether it takes images, whether it takes video) that
  `OpenRouterChatClient` resolves for `options.ModelId ?? model`, so a patched turn is decided by
  the patched model. A model that takes video gets the clip and none of its stills. A model that
  takes only images gets the stills and every other picture, never the clip.
- **A turn whose model would see nothing of the clip is refused.** That is a model that takes
  neither, or one that takes only images when the clip came with no stills. `AttachmentRefusedException`
  is thrown before the request leaves. It reaches the channel as the turn's error, worded with the
  model and the file. This is the one attachment check the agent makes itself: the channel cannot
  know the model a later turn resolves to, and leaving the clip out would give an answer that
  ignores what was sent. The shared `AttachmentCapability` rule refuses a clip only for a model
  that takes neither, since stills stand in for it elsewhere.
- **The clip obeys the same distance as every attachment**, so a follow-up turn resends it. Past
  the distance, one placeholder names the clip on any model, never one per frame, and nothing is
  refused.
- **On the wire, a clip is `{"type":"input_video","video_url":"data:<type>;base64,…"}`.**
  `VideoInputParts` turns each video `DataContent` into the adapter's raw `ResponseContentPart`,
  which the adapter passes through untouched. This happens last, on the copy that is sent, so
  hydration and truncation still see a `DataContent`. The truncator counts a clip at a flat 6,000
  tokens, because a provider bills a clip by its running time and not by its bytes.

## Considered options

**The channel decides, sending the clip or the stills.** Rejected: the channel cannot know the
model a turn will resolve to. A patch or a change of the agent's model would then put a clip in
front of a model that cannot take it.

**Stills as a hidden part of the clip's reference, not attachments of their own.** Rejected. The
stills need bytes the channel serves and the transcript shows, as every attachment already has.
Naming them by id keeps one fetch path and one placeholder shape.

**Rewriting the body in `OpenRouterHttpHelpers`.** That would mean finding a marker part in the
serialized request and replacing it with a video part. Rejected: the adapter drops a video part
before the body exists, so there is nothing to find. The raw part is the adapter's own way in.

## Consequences

- **Not verified live.** OpenRouter's Responses page shows `input_video` with a YouTube URL only. A
  base64 data URL is documented for Chat Completions' `video_url`, and that is what the standalone
  Sugary benchmark sent Gemini successfully. If the Responses wire refuses a data URL, the fallback
  is to change only `VideoInputParts`, for example to an uploaded URL. The ticket 08 eval and the
  ticket 12 E2E are the tripwire.
- WebChat and Telegram produce no clips. Telegram keeps refusing a video, because a clip needs a
  channel that normalises it and takes its stills.
- The agent now refuses an attachment turn itself in one case. Image and document refusals stay
  where they were, at the composer and the channel servers.
