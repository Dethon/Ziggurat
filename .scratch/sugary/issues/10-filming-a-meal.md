# 10 — Filming a meal

**What to build:** In the app she films a short clip around the plate. The Sugary channel normalises it on arrival (H.264 in MP4, at most 540 lines, no audio, at most 20 seconds, a longer clip refused with that reason) with ffmpeg in its container, derives about eight sharp, evenly spaced stills at the app's photo size, and sends the clip with its stills on the turn. Gemini 3.8 Flash gets the clip; an image-only model gets the overhead photo and the stills. The 14 Nutrition5k video cases join the eval tier.

**Blocked by:** 05, 08, 09

**Status:** ready-for-agent

Spec: `.scratch/sugary/spec.md`

- [ ] Clips in the formats phones record (MP4 with H.264 or HEVC, WebM) are accepted and normalised; the sound never leaves the server.
- [ ] An over-long clip is refused before anything is sent, and the app says why.
- [ ] The stills skip blurred frames and are stored as attachments of the same message, named by the clip's reference.
- [ ] On Gemini 3.8 Flash the request carries the clip; with the agent patched to Haiku it carries the stills and photos.
- [ ] The eval tier reports video and photo apart; a first run is compared with the standalone figures (8.3 g with video against 11.2 g from a photo on Gemini 3.8 Flash).
